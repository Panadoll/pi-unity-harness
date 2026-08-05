using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Tests.Shared
{
    public sealed class GameViewPhysicsRaycastTests
    {
        private GameObject _cameraGo;
        private GameObject _cube;

        [SetUp]
        public void SetUp()
        {
            _cameraGo = new GameObject("TestMainCamera", typeof(Camera));
            _cameraGo.tag = "MainCamera";
            _cameraGo.transform.position = new Vector3(0f, 0f, -10f);
            _cameraGo.transform.LookAt(Vector3.zero);

            _cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cube.name = "ProbeCube";
            _cube.transform.position = Vector3.zero;
        }

        [TearDown]
        public void TearDown()
        {
            if (_cube != null)
                Object.DestroyImmediate(_cube);
            if (_cameraGo != null)
                Object.DestroyImmediate(_cameraGo);
        }

        [Test]
        public void ConvertInputToUnity_FlipsY()
        {
            var conversion = PiGameViewCoordinates.ConvertInputToUnity(new Vector2(10f, 20f), new Vector2(100f, 200f));
            Assert.That(conversion.InputPosition.x, Is.EqualTo(10f));
            Assert.That(conversion.InputPosition.y, Is.EqualTo(20f));
            Assert.That(conversion.UnityPosition.x, Is.EqualTo(10f));
            Assert.That(conversion.UnityPosition.y, Is.EqualTo(180f));
            Assert.That(conversion.GameViewSize.x, Is.EqualTo(100f));
            Assert.That(conversion.GameViewSize.y, Is.EqualTo(200f));
        }

        [Test]
        public void RaycastFromInput_HitsCubeInFrontOfCamera()
        {
            Camera cam = _cameraGo.GetComponent<Camera>();
            // Center of a 100x100 view: unity position (50,50) looks near forward axis for orthographic-ish usage.
            // Use ScreenPointToRay via a known conversion: pick center of game view size.
            Vector2 size = new Vector2(cam.pixelWidth > 0 ? cam.pixelWidth : 640, cam.pixelHeight > 0 ? cam.pixelHeight : 480);
            Vector2 input = new Vector2(size.x * 0.5f, size.y * 0.5f);

            // Force conversion size by temporarily relying on camera.pixel size path via helper overload.
            var conversion = PiGameViewCoordinates.ConvertInputToUnity(input, size);
            Physics.SyncTransforms();
            Ray ray = cam.ScreenPointToRay(conversion.UnityPosition);
            Assert.That(Physics.Raycast(ray, out RaycastHit hit, 1000f), Is.True, "Expected physics hit against cube");
            Assert.That(hit.collider.gameObject.name, Is.EqualTo("ProbeCube"));

            var result = PiGameViewPhysicsRaycast.RaycastFromInput(input, 1000f, Physics.DefaultRaycastLayers, true, cam);
            // Camera.main may not match if GameView size differs; use explicit camera path via public API which uses Camera.main.
            // Ensure Camera.main is our test camera:
            Assert.That(Camera.main, Is.Not.Null);
            string json = PiGameViewPhysicsRaycast.ToJson(
                PiGameViewPhysicsRaycast.RaycastFromInput(input.x, input.y, 1000f, Physics.DefaultRaycastLayers, true, cam));

            Assert.That(json, Does.Contain("\"schema\":\"harness.input.raycast.v1\""));
            Assert.That(json, Does.Contain("\"input_coordinate_system\":\"top_left_game_view\""));
            Assert.That(json, Does.Contain("\"unity_coordinate_system\":\"bottom_left_game_view\""));
            Assert.That(json, Does.Contain("\"camera_found\":true"));
        }

        [Test]
        public void RaycastJson_InvalidMaxDistance_FailsUsage()
        {
            var result = PiGameViewPhysicsRaycast.RaycastFromInput(1f, 1f, 0f);
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorType, Is.EqualTo("usage"));
            string json = PiGameViewPhysicsRaycast.ToJson(result);
            Assert.That(json, Does.Contain("\"status\":\"failed\""));
            Assert.That(json, Does.Contain("MaxDistance"));
        }

        [Test]
        public void ProbeJson_ContainsWouldHitFields()
        {
            string json = PiInputProbe.ToJson(PiInputProbe.ProbeAt(10f, 10f, includePhysics: true));
            Assert.That(json, Does.Contain("\"schema\":\"harness.input.probe.v1\""));
            Assert.That(json, Does.Contain("\"would_hit\""));
            Assert.That(json, Does.Contain("\"would_hit_kind\""));
            Assert.That(json, Does.Contain("\"input_coordinate_system\""));
            Assert.That(json, Does.Contain("\"physics_ran\":true"));
        }
    }
}
