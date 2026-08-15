#if PI_UNITY_PIPELINE
using System;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    /// <summary>
    /// 对象身份解析层测试：instanceId 在 JSON 中以长整型数字传输（不截断），
    /// 解析兼容 int 与 long；Unity 6.5 EntityId（超出 int 范围）不被截断。
    /// 参考 UniCli UnityObjectIdentity。
    /// </summary>
    public sealed class UnityObjectIdentityTests
    {
        [TearDown]
        public void TearDown()
        {
            // 清理测试中创建的对象
            foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (go.name.StartsWith("PiIdentityTest_", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ─── wire format：long 不截断 ───────────────────────────────

        [Test]
        public void Json_IntValues_RoundTripThroughLong()
        {
            AssertRoundTrip(0L);
            AssertRoundTrip(1L);
            AssertRoundTrip(-1L);
            AssertRoundTrip(123456L);
            AssertRoundTrip(-123456L);
            AssertRoundTrip(int.MaxValue);
            AssertRoundTrip(int.MinValue);
        }

        [Test]
        public void Json_LongValuesBeyondIntRange_RoundTripWithoutTruncation()
        {
            AssertRoundTrip((long)int.MaxValue + 1);
            AssertRoundTrip((long)int.MinValue - 1);
            AssertRoundTrip(5497558178164L); // 当前测试工程实例中观察到的 EntityId 数量级
            AssertRoundTrip(long.MaxValue);
        }

        [Test]
        public void Json_SerializedAsNumber_NotString()
        {
            string json = JsonConvert.SerializeObject(new { instanceId = 5497558178164L });
            Assert.That(json, Does.Contain("\"instanceId\":5497558178164"), "long instanceId 应以数字输出，不被截断");
            // 值位置不应带引号（不是字符串 "5497558178164"）
            Assert.That(json, Does.Not.Contain("\"instanceId\":\"5497558178164\""), "instanceId 不应被序列化为字符串");
        }

        [Test]
        public void Parse_IntJson_ResolvesToLong()
        {
            // 旧客户端发送 int（含负数与 int.MinValue 边界）也能被解析为 long
            Assert.AreEqual(int.MinValue, ParseInstanceId("{\"instance_id\":" + int.MinValue + "}"));
            Assert.AreEqual(-42L, ParseInstanceId("{\"instance_id\":-42}"));
            Assert.AreEqual(42L, ParseInstanceId("{\"instance_id\":42}"));
        }

        [Test]
        public void Parse_LongJson_BeyondIntRange_KeepsFullValue()
        {
            Assert.AreEqual(5497558178164L, ParseInstanceId("{\"instance_id\":5497558178164}"));
            Assert.AreEqual((long)int.MaxValue + 1, ParseInstanceId("{\"instance_id\":" + ((long)int.MaxValue + 1) + "}"));
        }

        // ─── 解析路径：Resolve ──────────────────────────────────────

        [Test]
        public void GetId_ThenResolve_ReturnsSameObject()
        {
            var go = CreateTestGameObject("PiIdentityTest_Go1");
            long id = UnityObjectIdentity.GetId(go);
            Assert.That(id, Is.Not.EqualTo(0), "对象 id 不应为 0");

            var resolved = UnityObjectIdentity.Resolve(id);
            Assert.AreSame(go, resolved, "GetId -> Resolve 应往返一致");
        }

        [Test]
        public void GetId_ThenResolveGeneric_ReturnsTypedObject()
        {
            var go = CreateTestGameObject("PiIdentityTest_Go2");
            var resolved = UnityObjectIdentity.Resolve<GameObject>(UnityObjectIdentity.GetId(go));
            Assert.AreSame(go, resolved);
        }

        [Test]
        public void GetId_ThenResolve_RoundTripsComponent()
        {
            var go = CreateTestGameObject("PiIdentityTest_Go3");
            var collider = go.AddComponent<BoxCollider>();
            var resolved = UnityObjectIdentity.Resolve(UnityObjectIdentity.GetId(collider));
            Assert.AreSame(collider, resolved);
        }

        [Test]
        public void Resolve_Zero_ReturnsNull()
        {
            Assert.IsNull(UnityObjectIdentity.Resolve(0L));
        }

        [Test]
        public void ResolveGeneric_MismatchedType_ReturnsNull()
        {
            var go = CreateTestGameObject("PiIdentityTest_Go4");
            var collider = go.AddComponent<BoxCollider>();
            var resolved = UnityObjectIdentity.Resolve<Rigidbody>(UnityObjectIdentity.GetId(collider));
            Assert.IsNull(resolved);
        }

        [Test]
        public void Resolve_OutOfIntRange_OnLegacyPath_ReturnsNullOrEntityIdPath()
        {
            // 兼容行为：超出 int 范围的 id 在旧版本（无 EntityId）下应解析为 null；
            // 在 Unity 6.5（EntityId）下按原始值解析。两种路径都不应抛异常。
            Assert.DoesNotThrow(() => UnityObjectIdentity.Resolve(long.MaxValue));
            Assert.DoesNotThrow(() => UnityObjectIdentity.Resolve(long.MinValue));
        }

        // ─── helpers ────────────────────────────────────────────────

        private static void AssertRoundTrip(long id)
        {
            string json = JsonConvert.SerializeObject(new { instanceId = id });
            var parsed = JObject.Parse(json);
            Assert.AreEqual(id, parsed["instanceId"].Value<long>(), "id " + id + " JSON 往返应一致");
        }

        private static long ParseInstanceId(string json)
        {
            return JObject.Parse(json)["instance_id"].Value<long>();
        }

        private static GameObject CreateTestGameObject(string name)
        {
            var go = new GameObject(name);
            return go;
        }
    }
}
#endif
