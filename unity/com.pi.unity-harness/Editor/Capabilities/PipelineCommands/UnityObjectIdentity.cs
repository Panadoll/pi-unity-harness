#if PI_UNITY_PIPELINE
using System;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    /// <summary>
    /// 统一的对象身份解析层（参考 UniCli UnityObjectIdentity）。
    /// wire format 为 long：Unity 6.5（6000.5+）上携带完整 EntityId 原始值
    /// （EntityId.ToULong / FromULong，不截断高 32 位）；旧版本为 int instance id
    /// 加宽到 long。JSON 中始终以长整型数字表示，解析兼容 int 与 long。
    ///
    /// 边界取 UNITY_6000_5_OR_NEWER（与 UniCli 一致）：GetEntityId 确认存在于
    /// 6000.5，6000.0 不存在，6000.1-6000.4 未验证故回退 legacy API。
    /// 条件编译保证 2021.3/2022/6000.0-6000.4 不引用 6.5 类型，避免硬依赖。
    /// </summary>
    public static class UnityObjectIdentity
    {
        /// <summary>
        /// 取对象的 64 位身份 id：EntityId 环境下取原始 EntityId 值（不截断），
        /// 否则取 int instance id 加宽为 long。
        /// </summary>
        public static long GetId(UnityEngine.Object obj)
        {
            if (obj == null)
                return 0L;

#if UNITY_6000_5_OR_NEWER
            // EntityId 为 64 位（6000.5+），原始值高位有意义，绝不截断；
            // long 是对 ulong 的位级重解释（unchecked），Resolve 可还原同一位模式。
            return unchecked((long)EntityId.ToULong(obj.GetEntityId()));
#else
            return obj.GetInstanceID();
#endif
        }

        /// <summary>
        /// 按 id 解析对象：EntityId 环境下按原始 EntityId 值查找；
        /// 旧版本先做 int 范围检查（超出即视为不存在），再走 InstanceIDToObject。
        /// </summary>
        public static UnityEngine.Object Resolve(long id)
        {
            if (id == 0L)
                return null;

#if UNITY_6000_5_OR_NEWER
            // 原始值查找：与 GetId 互为逆运算，位级还原（即使最高位被置位，long 显示为负数也不丢失）。
            return EditorUtility.EntityIdToObject(EntityId.FromULong(unchecked((ulong)id)));
#else
            // 旧版本 id 恒为 int 范围；超出范围视为不存在的对象。
            if (id < int.MinValue || id > int.MaxValue)
                return null;
            return EditorUtility.InstanceIDToObject((int)id);
#endif
        }

        /// <summary>
        /// 泛型解析：类型不匹配返回 null。
        /// </summary>
        public static T Resolve<T>(long id) where T : UnityEngine.Object
            => Resolve(id) as T;
    }
}
#endif
