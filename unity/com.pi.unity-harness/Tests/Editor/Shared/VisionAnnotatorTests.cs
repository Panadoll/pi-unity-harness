using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Shared;

namespace Pi.UnityHarness.Editor.Tests.Shared
{
    public sealed class VisionAnnotatorTests
    {
        [Test]
        public void Collect_ReturnsCoordinateSystems()
        {
            var set = PiVisionAnnotator.Collect(includeUi: false, includePhysics: false);
            Assert.That(set.InputCoordinateSystem, Is.EqualTo(PiGameViewCoordinates.InputCoordinateSystem));
            Assert.That(set.UnityCoordinateSystem, Is.EqualTo(PiGameViewCoordinates.UnityCoordinateSystem));
            Assert.That(set.ConversionFormula, Is.EqualTo(PiGameViewCoordinates.ConversionFormula));
        }

        [Test]
        public void AttachToCaptureJson_InjectsAnnotations()
        {
            string capture = "{\"status\":\"succeeded\",\"schema\":\"harness.vision.capture.v1\",\"path\":\"/tmp/a.png\",\"source\":\"scene\",\"width\":10,\"height\":10,\"bytes\":1}";
            var set = new PiVisionAnnotationSet
            {
                GameViewWidth = 100,
                GameViewHeight = 50,
                InputCoordinateSystem = PiGameViewCoordinates.InputCoordinateSystem,
                UnityCoordinateSystem = PiGameViewCoordinates.UnityCoordinateSystem,
                ConversionFormula = PiGameViewCoordinates.ConversionFormula,
            };
            set.Physics.Add(new PiVisionPhysicsAnnotation
            {
                Label = "R1",
                Name = "Cube",
                Path = "Cube",
                InputX = 10,
                InputY = 20,
                Layer = 0,
                LayerName = "Default",
                Distance = 1.5f,
            });

            string merged = PiVisionAnnotator.AttachToCaptureJson(capture, set, drawOnImage: false);
            Assert.That(merged, Does.Contain("\"annotations\""));
            Assert.That(merged, Does.Contain("\"label\":\"R1\""));
            Assert.That(merged, Does.Contain("\"input_coordinate_system\""));
            Assert.That(merged, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(merged.EndsWith("}"), Is.True);
        }

        [Test]
        public void ToJsonObject_IncludesSchemaAndArrays()
        {
            var set = new PiVisionAnnotationSet
            {
                GameViewWidth = 800,
                GameViewHeight = 600,
            };
            string json = PiVisionAnnotator.ToJsonObject(set);
            Assert.That(json, Does.Contain("\"schema\":\"harness.vision.annotations.v1\""));
            Assert.That(json, Does.Contain("\"ui\":[]"));
            Assert.That(json, Does.Contain("\"physics\":[]"));
        }
    }
}
