using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Vision;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Tests.Playtest
{
    /// <summary>
    /// playtest-loop-borrow-plan Phase 1 纯算法契约：0-1000 网格像素映射、
    /// dHash 指纹稳定性、帧差异判定、网格叠图、时间序列合成、JSON schema。
    /// 全部为 Editor 可测的纯函数，不依赖真实渲染帧。
    /// </summary>
    public sealed class PlaytestVisionAlgorithmTests
    {
        private const string OutputDir = "Temp/Harness/playtest-tests";

        [TearDown]
        public void TearDown()
        {
            string root = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName, OutputDir);
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }

        // ─── 0-1000 网格像素映射（源 _make_coordinate_grid: x = round(v * (w-1) / 1000)）───

        [Test]
        public void NormalizedToPixel_EndpointsAndMid_MapExactly()
        {
            // 1001 宽：0 -> 0, 500 -> 500, 1000 -> 1000（无取整误差）
            Assert.That(PlaytestVision.NormalizedToPixel(0, 1001), Is.EqualTo(0));
            Assert.That(PlaytestVision.NormalizedToPixel(500, 1001), Is.EqualTo(500));
            Assert.That(PlaytestVision.NormalizedToPixel(1000, 1001), Is.EqualTo(1000));
        }

        [Test]
        public void NormalizedToPixel_ClampsOutOfRange()
        {
            Assert.That(PlaytestVision.NormalizedToPixel(-5, 1280), Is.EqualTo(0));
            Assert.That(PlaytestVision.NormalizedToPixel(1500, 1280), Is.EqualTo(1279));
        }

        [Test]
        public void NormalizedToPixel_RoundsNearest()
        {
            // 1280 宽：500 -> round(500*1279/1000) = round(639.5) = 640
            Assert.That(PlaytestVision.NormalizedToPixel(500, 1280), Is.EqualTo(640));
        }

        // ─── dHash 指纹（17x16 灰度、行内相邻比较、64 hex）───

        [Test]
        public void DHash_SameTexture_IsDeterministic()
        {
            var texture = CreateSolid(64, 64, new Color(0.3f, 0.6f, 0.9f));
            string first = PlaytestVision.DHashFingerprint(texture);
            string second = PlaytestVision.DHashFingerprint(texture);
            Assert.That(first, Is.EqualTo(second));
            Assert.That(first, Does.Match("^[0-9a-f]{64}$"));
        }

        [Test]
        public void DHash_SolidColor_IsDeterministic()
        {
            var texture = CreateSolid(128, 72, new Color(0.1f, 0.1f, 0.1f));
            Assert.That(PlaytestVision.DHashFingerprint(texture), Is.EqualTo(PlaytestVision.DHashFingerprint(texture)));
        }

        [Test]
        public void DHash_SinglePixelPerturbation_FlipsFewBits()
        {
            var baseTexture = CreateSolid(96, 96, new Color(0.5f, 0.5f, 0.5f));
            string baseline = PlaytestVision.DHashFingerprint(baseTexture);

            var perturbed = CreateSolid(96, 96, new Color(0.5f, 0.5f, 0.5f));
            perturbed.SetPixel(50, 50, new Color(0.9f, 0.9f, 0.9f));
            perturbed.Apply();
            string changed = PlaytestVision.DHashFingerprint(perturbed);

            int flipped = CountDifferentBits(baseline, changed);
            // 微扰只允许翻转少量指纹位，否则"重复画面计数"会因噪声抖动
            Assert.That(flipped, Is.LessThanOrEqualTo(8),
                $"单像素微扰不应大幅改变 dHash。翻转位: {flipped}");
        }

        [Test]
        public void DHash_DifferentContent_Differs()
        {
            // 纯色图对 dHash 无信息量（行内比较恒等），用有结构的图验证区分度
            var stripes = CreateStripes(64, 64);
            var gradient = CreateGradient(64, 64);
            string stripesHash = PlaytestVision.DHashFingerprint(stripes);
            string gradientHash = PlaytestVision.DHashFingerprint(gradient);
            Assert.That(stripesHash, Is.Not.EqualTo(gradientHash));
            Assert.That(stripesHash, Is.Not.EqualTo("0000000000000000000000000000000000000000000000000000000000000000"));
        }

        [Test]
        public void DHash_UsesLuminance_NotJustRedChannel()
        {
            // dHash 必须按亮度 0.299R+0.587G+0.114B 比较：
            // R 相同、仅 G/B 不同的画面（如 UI 高亮、过场色调）指纹必须不同
            var striped = CreateStripesWithLuma(64, 64);   // R 恒定，G/B 条纹变化 → 亮度有结构
            var flat = CreateSolid(64, 64, new Color(0.5f, 0.55f, 0.55f)); // R 相同，亮度近似平均
            Assert.That(
                PlaytestVision.DHashFingerprint(striped),
                Is.Not.EqualTo(PlaytestVision.DHashFingerprint(flat)));
        }

        // ─── 帧差异判定（64x36 缩略绝对差）───

        [Test]
        public void ComputeChanged_IdenticalThumbs_IsFalse()
        {
            var a = CreateSolid(64, 36, Color.black);
            Assert.That(PlaytestVision.ComputeChanged(a, a), Is.False);
        }

        [Test]
        public void ComputeChanged_DifferentThumbs_IsTrue()
        {
            var a = CreateSolid(64, 36, Color.black);
            var b = CreateSolid(64, 36, Color.white);
            Assert.That(PlaytestVision.ComputeChanged(a, b), Is.True);
        }

        // ─── 网格叠图（0-1000 坐标尺，每 100 一格）───

        [Test]
        public void DrawNormalizedGrid_DrawsVerticalLineAtExpectedPixel()
        {
            // 200x100：x = round(100 * 199 / 1000) = round(19.9) = 20
            var texture = CreateSolid(200, 100, new Color(0.8f, 0.8f, 0.8f));
            PlaytestVision.DrawNormalizedGrid(texture);

            var probe = texture.GetPixel(20, 50);
            Assert.That(probe.a, Is.GreaterThan(0.1f),
                "网格竖线应画在归一化 100 对应的像素列。实际 alpha=" + probe.a);
            Assert.That(probe.r, Is.LessThan(0.5f), "网格线应为青色系（低红分量）");
        }

        [Test]
        public void DrawNormalizedGrid_KeepsUnderlyingImage()
        {
            // 网格是叠加在截图上喂给 VLM 的视觉尺，绝不能清空原图
            var texture = CreateSolid(200, 100, new Color(0.9f, 0.2f, 0.3f));
            PlaytestVision.DrawNormalizedGrid(texture);

            var center = texture.GetPixel(110, 55);
            Assert.That(center.r, Is.GreaterThan(0.8f), "空白区应保留原图红色分量");
            Assert.That(center.g, Is.LessThan(0.3f), "空白区应保留原图绿色分量");
            Assert.That(center.b, Is.LessThan(0.4f), "空白区应保留原图蓝色分量");
        }

        [Test]
        public void DrawNormalizedGrid_DoesNotTouchCornerPixels()
        {
            var texture = CreateSolid(200, 100, Color.white);
            PlaytestVision.DrawNormalizedGrid(texture);

            // 空白区 (160,70) 与 (75,60) 不应有网格像素（线宽 1；
            // 竖线在 x=20,40,...,180，横线在 y=10,20,...,90，标签块在顶部/左侧边缘）
            Assert.That(texture.GetPixel(75, 60).a, Is.GreaterThan(0.9f));
            Assert.That(texture.GetPixel(160, 70).a, Is.GreaterThan(0.9f));
        }

        // ─── 时间序列合成 ───

        [Test]
        public void ComposeSheet_ThreeFrames_WritesFileWithLabels()
        {
            var frames = new List<Texture2D>
            {
                CreateSolid(64, 36, Color.red),
                CreateSolid(64, 36, Color.green),
                CreateSolid(64, 36, Color.blue),
            };
            var labels = new List<string> { "FRAME 1 / 3", "FRAME 2 / 3", "FRAME 3 / 3" };
            string outputPath = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName,
                OutputDir, "timeline_test.jpg");

            string written = PlaytestVision.ComposeSheet(frames, labels, 128, outputPath);

            Assert.That(written, Is.EqualTo(outputPath));
            Assert.That(File.Exists(outputPath), Is.True);
            var info = new FileInfo(outputPath);
            Assert.That(info.Length, Is.GreaterThan(0));

            var decoded = new Texture2D(2, 2);
            Assert.That(decoded.LoadImage(File.ReadAllBytes(outputPath)), Is.True);
            // 3 张 128 宽缩略 + 标签条：宽 > 384，高 > 标签条
            Assert.That(decoded.width, Is.GreaterThan(128 * 3));
            Assert.That(decoded.height, Is.GreaterThan(20));
        }

        // ─── JSON schema ───

        [Test]
        public void BuildObserveJson_ContainsSchemaAndFields()
        {
            string json = PlaytestVision.BuildObserveJson(
                status: "succeeded", error: null, errorType: null,
                frames: new List<string> { "f1.png", "f2.png" },
                latest: "f2.png", vision: "v.png", timeline: null,
                fingerprint: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                changed: true, timedOut: false, uniqueCount: 2,
                capturedCount: 3, deduplicatedCount: 1,
                gameViewWidth: 1280f, gameViewHeight: 720f,
                screenshotToGameviewX: 1f, screenshotToGameviewY: 1f,
                capturedAtUtc: "2026-01-01T00:00:00.000Z");

            StringAssert.Contains("\"schema\":\"harness.vision.observe.v1\"", json);
            StringAssert.Contains("\"status\":\"succeeded\"", json);
            StringAssert.Contains("\"fingerprint\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"", json);
            StringAssert.Contains("\"changed\":true", json);
            StringAssert.Contains("\"timed_out\":false", json);
            StringAssert.Contains("\"unique_count\":2", json);
            StringAssert.Contains("\"captured_count\":3", json);
            StringAssert.Contains("\"deduplicated_count\":1", json);
            StringAssert.Contains("\"frames\":[\"f1.png\",\"f2.png\"]", json);
            StringAssert.Contains("\"latest\":\"f2.png\"", json);
            StringAssert.Contains("\"vision\":\"v.png\"", json);
            StringAssert.Contains("\"timeline\":null", json);
            StringAssert.Contains("\"screenshot_to_gameview\"", json);
        }

        [Test]
        public void BuildObserveJson_FailureCarriesErrorType()
        {
            string json = PlaytestVision.BuildObserveJson(
                status: "failed", error: "requires PlayMode", errorType: "not_supported",
                frames: null, latest: null, vision: null, timeline: null,
                fingerprint: null, changed: false, timedOut: false, uniqueCount: 0,
                capturedCount: 0, deduplicatedCount: 0,
                gameViewWidth: 0f, gameViewHeight: 0f,
                screenshotToGameviewX: 0f, screenshotToGameviewY: 0f,
                capturedAtUtc: null);

            StringAssert.Contains("\"status\":\"failed\"", json);
            StringAssert.Contains("\"error_type\":\"not_supported\"", json);
            StringAssert.Contains("\"frames\":null", json);
        }

        [Test]
        public void BuildObserveJson_TimeoutCarriesTimedOutFlag()
        {
            string json = PlaytestVision.BuildObserveJson(
                status: "failed", error: "End-of-frame did not fire", errorType: "timeout",
                frames: null, latest: null, vision: null, timeline: null,
                fingerprint: null, changed: false, timedOut: true, uniqueCount: 0,
                capturedCount: 0, deduplicatedCount: 0,
                gameViewWidth: 0f, gameViewHeight: 0f,
                screenshotToGameviewX: 0f, screenshotToGameviewY: 0f,
                capturedAtUtc: null);

            StringAssert.Contains("\"error_type\":\"timeout\"", json);
            StringAssert.Contains("\"timed_out\":true", json);
        }

        [Test]
        public void BuildCaptureAfterJson_ContainsSchemaAndFields()
        {
            string json = PlaytestVision.BuildCaptureAfterJson(
                status: "succeeded", error: null, errorType: null,
                mode: "burst", sheet: "s.jpg",
                frames: new List<string> { "a0.png", "a1.png" },
                timingsMs: new List<int> { 0, 80 },
                fingerprint: "abcdef", timedOut: false, uniqueCount: 2, capturedCount: 2,
                capturedAtUtc: "2026-01-01T00:00:00.000Z");

            StringAssert.Contains("\"schema\":\"harness.vision.capture_after.v1\"", json);
            StringAssert.Contains("\"mode\":\"burst\"", json);
            StringAssert.Contains("\"sheet\":\"s.jpg\"", json);
            StringAssert.Contains("\"timings_ms\":[0,80]", json);
            StringAssert.Contains("\"timed_out\":false", json);
            StringAssert.Contains("\"unique_count\":2", json);
            StringAssert.Contains("\"captured_count\":2", json);
        }

        // ─── 非 PlayMode 前置（Editor 测试环境 Application.isPlaying == false）───

        [Test]
        public void Observe_GameMode_OutsidePlayMode_ReturnsNotSupported()
        {
            Assert.That(Application.isPlaying, Is.False, "本用例必须在 Editor（非 PlayMode）环境运行");

            var observe = PlaytestVision.ObserveJsonAsync(
                mode: "game", frames: 3, intervalMs: 100, overlay: "none",
                pathPrefix: OutputDir + "/outside_playmode");
            while (observe.MoveNext())
            {
            }
            string json = observe.Current as string;

            Assert.That(json, Is.Not.Null);
            StringAssert.Contains("\"status\":\"failed\"", json);
            StringAssert.Contains("\"error_type\":\"not_supported\"", json);
            StringAssert.Contains("PlayMode", json, "错误信息应提示需要 PlayMode");
        }

        [Test]
        public void Observe_InvalidMode_ReturnsUsage()
        {
            var observe = PlaytestVision.ObserveJsonAsync(
                mode: "scene", frames: 3, intervalMs: 100, overlay: "none",
                pathPrefix: OutputDir + "/bad_mode");
            while (observe.MoveNext())
            {
            }
            string json = observe.Current as string;

            Assert.That(json, Is.Not.Null);
            StringAssert.Contains("\"status\":\"failed\"", json);
            StringAssert.Contains("\"error_type\":\"usage\"", json);
        }

        // ─── helpers ───

        private static Texture2D CreateSolid(int width, int height, Color color)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static Texture2D CreateStripesWithLuma(int width, int height)
        {
            // R 恒定 0.5；G/B 按列交替 0.2/0.9，制造亮度条纹（只用 R 通道无法区分）
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float gb = (x / 8) % 2 == 0 ? 0.2f : 0.9f;
                    pixels[y * width + x] = new Color(0.5f, gb, gb);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static Texture2D CreateStripes(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = (x / 8) % 2 == 0 ? Color.white : Color.black;
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static Texture2D CreateGradient(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float value = x / (float)(width - 1);
                    pixels[y * width + x] = new Color(value, value, value);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static int CountDifferentBits(string a, string b)
        {
            // 64 hex 字符 → 256 bit，逐 bit 比较
            int flipped = 0;
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                int x = Convert.ToInt32(a[i].ToString(), 16);
                int y = Convert.ToInt32(b[i].ToString(), 16);
                int diff = x ^ y;
                while (diff != 0)
                {
                    flipped += diff & 1;
                    diff >>= 1;
                }
            }
            return flipped;
        }
    }
}
