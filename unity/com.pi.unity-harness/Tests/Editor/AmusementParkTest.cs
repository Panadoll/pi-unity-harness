using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityInput = UnityEngine.Input;

namespace Pi.UnityHarness.Editor.Tests
{
    /// <summary>
    /// 乐园场景测试 - 创建一个可交互的乐园环境
    /// 包含：地面、玩家控制、NPC、娱乐设备
    /// </summary>
    public class AmusementParkTest
    {
        private GameObject parkRoot;
        private GameObject player;
        private List<GameObject> npcs = new List<GameObject>();
        private List<GameObject> rides = new List<GameObject>();
        
        [Test]
        public void CreateAmusementPark_SceneIsValid()
        {
            // 创建乐园根节点
            parkRoot = new GameObject("AmusementPark");
            
            // 1. 创建地面
            CreateGround();
            
            // 2. 创建围栏和装饰
            CreateFences();
            CreateDecorations();
            
            // 3. 创建娱乐设备
            CreateFerrisWheel();
            CreateCarousel();
            CreateRollerCoaster();
            CreateSwings();
            
            // 4. 创建NPC
            CreateNPCs();
            
            // 5. 创建玩家
            CreatePlayer();
            
            // 6. 创建UI
            CreateGameUI();
            
            // 验证场景结构
            Assert.IsNotNull(parkRoot, "乐园根节点应该存在");
            Assert.IsNotNull(player, "玩家应该存在");
            Assert.That(npcs.Count, Is.EqualTo(5), "应该有5个NPC");
            Assert.That(rides.Count, Is.EqualTo(4), "应该有4个娱乐设备");
            
            Debug.Log("=== 乐园场景创建成功 ===");
            Debug.Log($"  玩家: {player.name}");
            Debug.Log($"  NPC数量: {npcs.Count}");
            Debug.Log($"  娱乐设备: {rides.Count}");
            Debug.Log("  控制方式: WASD移动, 空格跳跃");
        }
        
        /// <summary>
        /// 创建地面
        /// </summary>
        private void CreateGround()
        {
            // 主地面
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.SetParent(parkRoot.transform);
            ground.transform.position = new Vector3(0, -0.5f, 0);
            ground.transform.localScale = new Vector3(100, 1, 100);
            
            var groundRenderer = ground.GetComponent<Renderer>();
            var groundMat = new Material(Shader.Find("Standard"));
            groundMat.color = new Color(0.4f, 0.8f, 0.4f); // 绿色草地
            groundRenderer.material = groundMat;
            
            // 道路
            CreatePath(new Vector3(0, 0.01f, 0), new Vector3(60, 0.02f, 3));
            CreatePath(new Vector3(0, 0.01f, 0), new Vector3(3, 0.02f, 60));
            CreatePath(new Vector3(20, 0.01f, 20), new Vector3(3, 0.02f, 30));
            CreatePath(new Vector3(-20, 0.01f, -20), new Vector3(30, 0.02f, 3));
        }
        
        /// <summary>
        /// 创建道路
        /// </summary>
        private void CreatePath(Vector3 position, Vector3 scale)
        {
            var path = GameObject.CreatePrimitive(PrimitiveType.Cube);
            path.name = "Path";
            path.transform.SetParent(parkRoot.transform);
            path.transform.position = position;
            path.transform.localScale = scale;
            
            var renderer = path.GetComponent<Renderer>();
            var mat = new Material(Shader.Find("Standard"));
            mat.color = new Color(0.8f, 0.7f, 0.5f); // 土路颜色
            renderer.material = mat;
        }
        
        /// <summary>
        /// 创建围栏
        /// </summary>
        private void CreateFences()
        {
            var fenceParent = new GameObject("Fences");
            fenceParent.transform.SetParent(parkRoot.transform);
            
            // 四周围栏
            for (int i = 0; i < 4; i++)
            {
                var fence = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fence.name = $"Fence_{i}";
                fence.transform.SetParent(fenceParent.transform);
                
                float xPos = (i % 2 == 0) ? (i == 0 ? -30 : 30) : 0;
                float zPos = (i % 2 == 1) ? (i == 1 ? -30 : 30) : 0;
                float xScale = (i % 2 == 1) ? 60 : 1;
                float zScale = (i % 2 == 0) ? 60 : 1;
                
                fence.transform.position = new Vector3(xPos, 1, zPos);
                fence.transform.localScale = new Vector3(xScale, 2, zScale);
                
                var renderer = fence.GetComponent<Renderer>();
                var mat = new Material(Shader.Find("Standard"));
                mat.color = new Color(0.6f, 0.4f, 0.2f); // 木栅栏
                renderer.material = mat;
            }
            
            // 入口门
            var gate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gate.name = "Gate";
            gate.transform.SetParent(fenceParent.transform);
            gate.transform.position = new Vector3(0, 1.5f, -30);
            gate.transform.localScale = new Vector3(8, 3, 1);
            
            var gateRenderer = gate.GetComponent<Renderer>();
            var gateMat = new Material(Shader.Find("Standard"));
            gateMat.color = Color.red;
            gateRenderer.material = gateMat;
        }
        
        /// <summary>
        /// 创建装饰物
        /// </summary>
        private void CreateDecorations()
        {
            var decoParent = new GameObject("Decorations");
            decoParent.transform.SetParent(parkRoot.transform);
            
            // 树木
            for (int i = 0; i < 20; i++)
            {
                CreateTree(decoParent.transform, 
                    new Vector3(Random.Range(-40, 40), 0, Random.Range(-40, 40)));
            }
            
            // 花坛
            for (int i = 0; i < 8; i++)
            {
                CreateFlowerBed(decoParent.transform,
                    new Vector3(Random.Range(-25, 25), 0, Random.Range(-25, 25)));
            }
            
            // 路灯
            for (int i = 0; i < 10; i++)
            {
                CreateLampPost(decoParent.transform,
                    new Vector3(Random.Range(-20, 20), 0, Random.Range(-20, 20)));
            }
        }
        
        private void CreateTree(Transform parent, Vector3 position)
        {
            var tree = new GameObject("Tree");
            tree.transform.SetParent(parent);
            tree.transform.position = position;
            
            // 树干
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            trunk.transform.SetParent(tree.transform);
            trunk.transform.localPosition = new Vector3(0, 2, 0);
            trunk.transform.localScale = new Vector3(0.5f, 2, 0.5f);
            
            var trunkRenderer = trunk.GetComponent<Renderer>();
            var trunkMat = new Material(Shader.Find("Standard"));
            trunkMat.color = new Color(0.4f, 0.25f, 0.1f);
            trunkRenderer.material = trunkMat;
            
            // 树冠
            var crown = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            crown.name = "Crown";
            crown.transform.SetParent(tree.transform);
            crown.transform.localPosition = new Vector3(0, 4.5f, 0);
            crown.transform.localScale = new Vector3(3, 3, 3);
            
            var crownRenderer = crown.GetComponent<Renderer>();
            var crownMat = new Material(Shader.Find("Standard"));
            crownMat.color = new Color(0.1f, 0.6f, 0.1f);
            crownRenderer.material = crownMat;
        }
        
        private void CreateFlowerBed(Transform parent, Vector3 position)
        {
            var bed = new GameObject("FlowerBed");
            bed.transform.SetParent(parent);
            bed.transform.position = position + Vector3.up * 0.3f;
            
            var baseObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            baseObj.name = "Base";
            baseObj.transform.SetParent(bed.transform);
            baseObj.transform.localScale = new Vector3(4, 0.6f, 4);
            
            var renderer = baseObj.GetComponent<Renderer>();
            var mat = new Material(Shader.Find("Standard"));
            mat.color = new Color(0.5f, 0.3f, 0.2f);
            renderer.material = mat;
            
            // 花朵
            Color[] flowerColors = { Color.red, Color.yellow, Color.magenta, Color.cyan };
            for (int i = 0; i < 8; i++)
            {
                var flower = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                flower.name = "Flower";
                flower.transform.SetParent(bed.transform);
                flower.transform.localPosition = new Vector3(
                    Random.Range(-1.5f, 1.5f), 
                    0.5f, 
                    Random.Range(-1.5f, 1.5f));
                flower.transform.localScale = Vector3.one * 0.3f;
                
                var flowerRenderer = flower.GetComponent<Renderer>();
                var flowerMat = new Material(Shader.Find("Standard"));
                flowerMat.color = flowerColors[i % flowerColors.Length];
                flowerRenderer.material = flowerMat;
            }
        }
        
        private void CreateLampPost(Transform parent, Vector3 position)
        {
            var lamp = new GameObject("LampPost");
            lamp.transform.SetParent(parent);
            lamp.transform.position = position;
            
            // 灯柱
            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Pole";
            pole.transform.SetParent(lamp.transform);
            pole.transform.localPosition = new Vector3(0, 3, 0);
            pole.transform.localScale = new Vector3(0.2f, 3, 0.2f);
            
            var poleRenderer = pole.GetComponent<Renderer>();
            var poleMat = new Material(Shader.Find("Standard"));
            poleMat.color = Color.gray;
            poleRenderer.material = poleMat;
            
            // 灯罩
            var lightBulb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            lightBulb.name = "LightBulb";
            lightBulb.transform.SetParent(lamp.transform);
            lightBulb.transform.localPosition = new Vector3(0, 5.5f, 0);
            lightBulb.transform.localScale = Vector3.one * 0.8f;
            
            var bulbRenderer = lightBulb.GetComponent<Renderer>();
            var bulbMat = new Material(Shader.Find("Standard"));
            bulbMat.color = Color.yellow;
            bulbRenderer.material = bulbMat;
            
            // 添加光源
            var light = lightBulb.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 10f;
            light.intensity = 0.5f;
            light.color = Color.yellow;
        }
        
        /// <summary>
        /// 创建摩天轮
        /// </summary>
        private void CreateFerrisWheel()
        {
            var wheel = new GameObject("FerrisWheel");
            wheel.transform.SetParent(parkRoot.transform);
            wheel.transform.position = new Vector3(-25, 0, 25);
            
            // 中心轴
            var center = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            center.name = "Center";
            center.transform.SetParent(wheel.transform);
            center.transform.localPosition = new Vector3(0, 15, 0);
            center.transform.localScale = new Vector3(1, 0.5f, 1);
            center.transform.rotation = Quaternion.Euler(0, 0, 90);
            
            var centerRenderer = center.GetComponent<Renderer>();
            var centerMat = new Material(Shader.Find("Standard"));
            centerMat.color = Color.red;
            centerRenderer.material = centerMat;
            
            // 支架
            for (int i = 0; i < 2; i++)
            {
                var support = GameObject.CreatePrimitive(PrimitiveType.Cube);
                support.name = $"Support_{i}";
                support.transform.SetParent(wheel.transform);
                support.transform.localPosition = new Vector3(i == 0 ? -2 : 2, 7.5f, 0);
                support.transform.localScale = new Vector3(0.5f, 15, 0.5f);
                support.transform.rotation = Quaternion.Euler(0, 0, i == 0 ? 15 : -15);
                
                var supportRenderer = support.GetComponent<Renderer>();
                var supportMat = new Material(Shader.Find("Standard"));
                supportMat.color = Color.gray;
                supportRenderer.material = supportMat;
            }
            
            // 轮辐和吊舱
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f * Mathf.Deg2Rad;
                float x = Mathf.Cos(angle) * 12;
                float y = Mathf.Sin(angle) * 12 + 15;
                
                // 轮辐
                var spoke = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                spoke.name = $"Spoke_{i}";
                spoke.transform.SetParent(wheel.transform);
                spoke.transform.localPosition = new Vector3(x / 2, y / 2 + 7.5f, 0);
                spoke.transform.localScale = new Vector3(0.1f, 7, 0.1f);
                spoke.transform.rotation = Quaternion.Euler(0, 0, i * 45);
                
                var spokeRenderer = spoke.GetComponent<Renderer>();
                var spokeMat = new Material(Shader.Find("Standard"));
                spokeMat.color = Color.white;
                spokeRenderer.material = spokeMat;
                
                // 吊舱
                var cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cabin.name = $"Cabin_{i}";
                cabin.transform.SetParent(wheel.transform);
                cabin.transform.localPosition = new Vector3(x, y + 7.5f, 0);
                cabin.transform.localScale = new Vector3(2, 2, 2);
                
                var cabinRenderer = cabin.GetComponent<Renderer>();
                var cabinMat = new Material(Shader.Find("Standard"));
                cabinMat.color = new Color(Random.value, Random.value, Random.value);
                cabinRenderer.material = cabinMat;
            }
            
            // 添加旋转脚本
            var rotator = wheel.AddComponent<FerrisWheelRotator>();
            
            rides.Add(wheel);
            Debug.Log("摩天轮创建完成");
        }
        
        /// <summary>
        /// 创建旋转木马
        /// </summary>
        private void CreateCarousel()
        {
            var carousel = new GameObject("Carousel");
            carousel.transform.SetParent(parkRoot.transform);
            carousel.transform.position = new Vector3(25, 0, 25);
            
            // 底座
            var basePlatform = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            basePlatform.name = "BasePlatform";
            basePlatform.transform.SetParent(carousel.transform);
            basePlatform.transform.localPosition = new Vector3(0, 0.5f, 0);
            basePlatform.transform.localScale = new Vector3(12, 0.5f, 12);
            
            var baseRenderer = basePlatform.GetComponent<Renderer>();
            var baseMat = new Material(Shader.Find("Standard"));
            baseMat.color = new Color(0.8f, 0.6f, 0.4f);
            baseRenderer.material = baseMat;
            
            // 中心柱
            var centerPole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            centerPole.name = "CenterPole";
            centerPole.transform.SetParent(carousel.transform);
            centerPole.transform.localPosition = new Vector3(0, 4, 0);
            centerPole.transform.localScale = new Vector3(1, 4, 1);
            
            var poleRenderer = centerPole.GetComponent<Renderer>();
            var poleMat = new Material(Shader.Find("Standard"));
            poleMat.color = Color.yellow;
            poleRenderer.material = poleMat;
            
            // 顶棚
            var roof = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            roof.name = "Roof";
            roof.transform.SetParent(carousel.transform);
            roof.transform.localPosition = new Vector3(0, 8, 0);
            roof.transform.localScale = new Vector3(13, 0.3f, 13);
            
            var roofRenderer = roof.GetComponent<Renderer>();
            var roofMat = new Material(Shader.Find("Standard"));
            roofMat.color = Color.red;
            roofRenderer.material = roofMat;
            
            // 木马
            Color[] horseColors = { Color.white, Color.brown, Color.black, Color.gray };
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f * Mathf.Deg2Rad;
                float x = Mathf.Cos(angle) * 5;
                float z = Mathf.Sin(angle) * 5;
                
                var horse = new GameObject($"Horse_{i}");
                horse.transform.SetParent(carousel.transform);
                horse.transform.localPosition = new Vector3(x, 1.5f, z);
                horse.transform.rotation = Quaternion.Euler(0, -i * 45, 0);
                
                // 马身
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                body.transform.SetParent(horse.transform);
                body.transform.localPosition = Vector3.zero;
                body.transform.localScale = new Vector3(0.8f, 0.5f, 1.2f);
                
                var bodyRenderer = body.GetComponent<Renderer>();
                var bodyMat = new Material(Shader.Find("Standard"));
                bodyMat.color = horseColors[i % horseColors.Length];
                bodyRenderer.material = bodyMat;
                
                // 马头
                var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                head.name = "Head";
                head.transform.SetParent(horse.transform);
                head.transform.localPosition = new Vector3(0, 0.3f, 0.8f);
                head.transform.localScale = new Vector3(0.5f, 0.5f, 0.7f);
                
                var headRenderer = head.GetComponent<Renderer>();
                headRenderer.material = bodyMat;
                
                // 支撑杆
                var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pole.name = "Pole";
                pole.transform.SetParent(horse.transform);
                pole.transform.localPosition = new Vector3(0, 2.5f, 0);
                pole.transform.localScale = new Vector3(0.1f, 2.5f, 0.1f);
                
                var horsePoleRenderer = pole.GetComponent<Renderer>();
                var horsePoleMat = new Material(Shader.Find("Standard"));
                horsePoleMat.color = Color.gray;
                horsePoleRenderer.material = horsePoleMat;
            }
            
            // 添加旋转脚本
            var rotator = carousel.AddComponent<CarouselRotator>();
            
            rides.Add(carousel);
            Debug.Log("旋转木马创建完成");
        }
        
        /// <summary>
        /// 创建过山车轨道
        /// </summary>
        private void CreateRollerCoaster()
        {
            var coaster = new GameObject("RollerCoaster");
            coaster.transform.SetParent(parkRoot.transform);
            coaster.transform.position = new Vector3(0, 0, -25);
            
            // 轨道点
            Vector3[] trackPoints = new Vector3[]
            {
                new Vector3(0, 1, 0),
                new Vector3(5, 3, -5),
                new Vector3(10, 8, -10),
                new Vector3(15, 12, -15),
                new Vector3(20, 8, -20),
                new Vector3(25, 3, -15),
                new Vector3(30, 1, -10),
                new Vector3(25, 1, -5),
                new Vector3(20, 1, 0),
                new Vector3(15, 1, 5),
                new Vector3(10, 1, 0),
                new Vector3(5, 1, -2),
            };
            
            // 创建轨道
            for (int i = 0; i < trackPoints.Length; i++)
            {
                var trackPiece = GameObject.CreatePrimitive(PrimitiveType.Cube);
                trackPiece.name = $"Track_{i}";
                trackPiece.transform.SetParent(coaster.transform);
                trackPiece.transform.localPosition = trackPoints[i];
                
                // 计算朝向
                Vector3 nextPoint = trackPoints[(i + 1) % trackPoints.Length];
                Vector3 direction = (nextPoint - trackPoints[i]).normalized;
                float distance = Vector3.Distance(trackPoints[i], nextPoint);
                
                trackPiece.transform.localScale = new Vector3(1, 0.3f, distance);
                trackPiece.transform.LookAt(coaster.transform.TransformPoint(nextPoint));
                
                var renderer = trackPiece.GetComponent<Renderer>();
                var mat = new Material(Shader.Find("Standard"));
                mat.color = Color.red;
                renderer.material = mat;
                
                // 支撑柱
                if (trackPoints[i].y > 2)
                {
                    var support = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    support.name = $"Support_{i}";
                    support.transform.SetParent(coaster.transform);
                    support.transform.localPosition = new Vector3(
                        trackPoints[i].x, 
                        trackPoints[i].y / 2, 
                        trackPoints[i].z);
                    support.transform.localScale = new Vector3(0.3f, trackPoints[i].y / 2, 0.3f);
                    
                    var supportRenderer = support.GetComponent<Renderer>();
                    var supportMat = new Material(Shader.Find("Standard"));
                    supportMat.color = Color.gray;
                    supportRenderer.material = supportMat;
                }
            }
            
            // 过山车车辆
            var cart = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cart.name = "Cart";
            cart.transform.SetParent(coaster.transform);
            cart.transform.localPosition = trackPoints[0] + Vector3.up * 0.5f;
            cart.transform.localScale = new Vector3(1.5f, 1, 2);
            
            var cartRenderer = cart.GetComponent<Renderer>();
            var cartMat = new Material(Shader.Find("Standard"));
            cartMat.color = Color.blue;
            cartRenderer.material = cartMat;
            
            // 添加运动脚本
            var movement = coaster.AddComponent<RollerCoasterMovement>();
            movement.trackPoints = trackPoints;
            movement.cart = cart;
            
            rides.Add(coaster);
            Debug.Log("过山车创建完成");
        }
        
        /// <summary>
        /// 创建秋千
        /// </summary>
        private void CreateSwings()
        {
            var swings = new GameObject("Swings");
            swings.transform.SetParent(parkRoot.transform);
            swings.transform.position = new Vector3(-25, 0, -25);
            
            // 顶部横杆
            var topBar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            topBar.name = "TopBar";
            topBar.transform.SetParent(swings.transform);
            topBar.transform.localPosition = new Vector3(0, 5, 0);
            topBar.transform.localScale = new Vector3(0.3f, 5, 0.3f);
            topBar.transform.rotation = Quaternion.Euler(0, 0, 90);
            
            var topBarRenderer = topBar.GetComponent<Renderer>();
            var topBarMat = new Material(Shader.Find("Standard"));
            topBarMat.color = Color.gray;
            topBarRenderer.material = topBarMat;
            
            // 创建3个秋千
            for (int i = 0; i < 3; i++)
            {
                var swing = new GameObject($"Swing_{i}");
                swing.transform.SetParent(swings.transform);
                swing.transform.localPosition = new Vector3(-3 + i * 3, 0, 0);
                
                // 绳子
                var rope = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                rope.name = "Rope";
                rope.transform.SetParent(swing.transform);
                rope.transform.localPosition = new Vector3(0, 2.5f, 0);
                rope.transform.localScale = new Vector3(0.05f, 2.5f, 0.05f);
                
                var ropeRenderer = rope.GetComponent<Renderer>();
                var ropeMat = new Material(Shader.Find("Standard"));
                ropeMat.color = new Color(0.6f, 0.4f, 0.2f);
                ropeRenderer.material = ropeMat;
                
                // 座板
                var seat = GameObject.CreatePrimitive(PrimitiveType.Cube);
                seat.name = "Seat";
                seat.transform.SetParent(swing.transform);
                seat.transform.localPosition = new Vector3(0, 0.2f, 0);
                seat.transform.localScale = new Vector3(1, 0.15f, 0.5f);
                
                var seatRenderer = seat.GetComponent<Renderer>();
                var seatMat = new Material(Shader.Find("Standard"));
                seatMat.color = new Color(0.4f, 0.25f, 0.1f);
                seatRenderer.material = seatMat;
                
                // 添加摆动脚本
                var swingAnim = swing.AddComponent<SwingAnimator>();
                swingAnim.delay = i * 0.5f;
            }
            
            rides.Add(swings);
            Debug.Log("秋千创建完成");
        }
        
        /// <summary>
        /// 创建NPC
        /// </summary>
        private void CreateNPCs()
        {
            var npcParent = new GameObject("NPCs");
            npcParent.transform.SetParent(parkRoot.transform);
            
            // NPC配置
            string[] npcNames = { "小明", "小红", "大叔", "阿姨", "小朋友" };
            Color[] npcColors = { Color.blue, Color.red, Color.green, Color.yellow, Color.cyan };
            Vector3[] npcPositions = 
            {
                new Vector3(10, 0, 10),
                new Vector3(-10, 0, -10),
                new Vector3(15, 0, -15),
                new Vector3(-15, 0, 15),
                new Vector3(0, 0, 20)
            };
            
            for (int i = 0; i < npcNames.Length; i++)
            {
                var npc = CreateNPC(npcNames[i], npcColors[i], npcPositions[i]);
                npc.transform.SetParent(npcParent.transform);
                npcs.Add(npc);
                
                // 添加巡逻脚本
                var patrol = npc.AddComponent<NPCPatrol>();
                patrol.centerPoint = npcPositions[i];
                patrol.patrolRadius = 10f;
            }
            
            Debug.Log($"创建了 {npcs.Count} 个NPC");
        }
        
        /// <summary>
        /// 创建单个NPC
        /// </summary>
        private GameObject CreateNPC(string name, Color color, Vector3 position)
        {
            var npc = new GameObject(name);
            npc.transform.position = position;
            // npc.tag = "NPC"; // 需要在TagManager中定义
            
            // 身体
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(npc.transform);
            body.transform.localPosition = new Vector3(0, 1, 0);
            
            var bodyRenderer = body.GetComponent<Renderer>();
            var bodyMat = new Material(Shader.Find("Standard"));
            bodyMat.color = color;
            bodyRenderer.material = bodyMat;
            
            // 头
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(npc.transform);
            head.transform.localPosition = new Vector3(0, 2, 0);
            head.transform.localScale = Vector3.one * 0.6f;
            
            var headRenderer = head.GetComponent<Renderer>();
            var headMat = new Material(Shader.Find("Standard"));
            headMat.color = new Color(0.9f, 0.75f, 0.6f); // 肤色
            headRenderer.material = headMat;
            
            // 名字标签
            var nameObj = new GameObject("NameTag");
            nameObj.transform.SetParent(npc.transform);
            nameObj.transform.localPosition = new Vector3(0, 2.8f, 0);
            
            // 使用3D文本显示名字（简化版）
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "NamePlate";
            cube.transform.SetParent(nameObj.transform);
            cube.transform.localScale = new Vector3(1.5f, 0.3f, 0.1f);
            
            var cubeRenderer = cube.GetComponent<Renderer>();
            var cubeMat = new Material(Shader.Find("Standard"));
            cubeMat.color = Color.white;
            cubeRenderer.material = cubeMat;
            
            return npc;
        }
        
        /// <summary>
        /// 创建玩家
        /// </summary>
        private void CreatePlayer()
        {
            player = new GameObject("Player");
            player.transform.position = new Vector3(0, 0, -25);
            // player.tag = "Player"; // 使用默认tag
            
            // 身体
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(player.transform);
            body.transform.localPosition = new Vector3(0, 1, 0);
            
            var bodyRenderer = body.GetComponent<Renderer>();
            var bodyMat = new Material(Shader.Find("Standard"));
            bodyMat.color = new Color(0.2f, 0.4f, 0.8f); // 蓝色
            bodyRenderer.material = bodyMat;
            
            // 头
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(player.transform);
            head.transform.localPosition = new Vector3(0, 2, 0);
            head.transform.localScale = Vector3.one * 0.6f;
            
            var headRenderer = head.GetComponent<Renderer>();
            var headMat = new Material(Shader.Find("Standard"));
            headMat.color = new Color(0.9f, 0.75f, 0.6f); // 肤色
            headRenderer.material = headMat;
            
            // 添加CharacterController
            var controller = player.AddComponent<CharacterController>();
            controller.center = new Vector3(0, 1, 0);
            controller.height = 2;
            controller.radius = 0.5f;
            
            // 添加玩家控制脚本
            var playerController = player.AddComponent<PlayerController>();
            
            // 创建相机跟随
            CreateCameraFollow();
            
            Debug.Log("玩家创建完成");
        }
        
        /// <summary>
        /// 创建跟随相机
        /// </summary>
        private void CreateCameraFollow()
        {
            // 清除现有相机
            var existingCam = Camera.main;
            if (existingCam != null)
            {
                existingCam.gameObject.name = "MainCamera_Old";
            }
            
            var cameraObj = new GameObject("FollowCamera");
            var camera = cameraObj.AddComponent<Camera>();
            camera.tag = "MainCamera";
            
            var cameraFollow = cameraObj.AddComponent<CameraFollow>();
            cameraFollow.target = player.transform;
            cameraFollow.offset = new Vector3(0, 8, -10);
            
            Debug.Log("跟随相机创建完成");
        }
        
        /// <summary>
        /// 创建游戏UI
        /// </summary>
        private void CreateGameUI()
        {
            var canvas = new GameObject("GameCanvas");
            var canvasComp = canvas.AddComponent<Canvas>();
            canvasComp.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.AddComponent<CanvasScaler>();
            canvas.AddComponent<GraphicRaycaster>();
            
            // 标题
            CreateUIText(canvas.transform, "TitleText", "游乐园", 
                new Vector2(0, 0), new Vector2(1, 1),
                new Vector2(0.5f, 0.95f), new Vector2(0.5f, 0.95f),
                24, Color.white);
            
            // 操作说明
            CreateUIText(canvas.transform, "HelpText", "WASD: 移动 | 空格: 跳跃 | 鼠标: 旋转视角", 
                new Vector2(0, 0), new Vector2(1, 1),
                new Vector2(0.5f, 0.05f), new Vector2(0.5f, 0.05f),
                14, Color.yellow);
            
            // NPC计数
            CreateUIText(canvas.transform, "NPCCount", $"NPC: {npcs.Count}", 
                new Vector2(0, 0), new Vector2(1, 1),
                new Vector2(0.1f, 0.95f), new Vector2(0.1f, 0.95f),
                16, Color.green);
            
            Debug.Log("UI创建完成");
        }
        
        private void CreateUIText(Transform parent, string name, string text,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pivotMin, Vector2 pivotMax,
            int fontSize, Color color)
        {
            var textObj = new GameObject(name);
            textObj.transform.SetParent(parent);
            
            var rectTransform = textObj.AddComponent<RectTransform>();
            rectTransform.anchorMin = anchorMin;
            rectTransform.anchorMax = anchorMax;
            rectTransform.pivot = pivotMin;
            rectTransform.anchoredPosition = Vector2.zero;
            rectTransform.sizeDelta = new Vector2(400, 50);
            
            var textComp = textObj.AddComponent<Text>();
            textComp.text = text;
            textComp.fontSize = fontSize;
            textComp.color = color;
            textComp.alignment = TextAnchor.MiddleCenter;
            textComp.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }
        
        [TearDown]
        public void Cleanup()
        {
            if (parkRoot != null)
            {
                Object.DestroyImmediate(parkRoot);
            }
        }
    }
    
    // ========== 运行时组件 ==========
    
    /// <summary>
    /// 玩家控制器
    /// </summary>
    public class PlayerController : MonoBehaviour
    {
        private CharacterController controller;
        private Vector3 velocity;
        private bool isGrounded;
        
        public float moveSpeed = 8f;
        public float jumpForce = 5f;
        public float gravity = -15f;
        public float rotationSpeed = 120f;
        
        private float rotationY = 0f;
        
        void Start()
        {
            controller = GetComponent<CharacterController>();
            if (controller == null)
            {
                controller = gameObject.AddComponent<CharacterController>();
                controller.center = new Vector3(0, 1, 0);
                controller.height = 2;
                controller.radius = 0.5f;
            }
        }
        
        void Update()
        {
            isGrounded = controller.isGrounded;
            
            if (isGrounded && velocity.y < 0)
            {
                velocity.y = -2f;
            }
            
            // 获取输入
            float horizontal = UnityInput.GetAxis("Horizontal");
            float vertical = UnityInput.GetAxis("Vertical");
            
            // 旋转
            float mouseX = UnityInput.GetAxis("Mouse X");
            rotationY += mouseX * rotationSpeed * Time.deltaTime;
            transform.rotation = Quaternion.Euler(0, rotationY, 0);
            
            // 移动
            Vector3 move = transform.right * horizontal + transform.forward * vertical;
            controller.Move(move * moveSpeed * Time.deltaTime);
            
            // 跳跃
            if (UnityInput.GetButtonDown("Jump") && isGrounded)
            {
                velocity.y = Mathf.Sqrt(jumpForce * -2f * gravity);
            }
            
            // 重力
            velocity.y += gravity * Time.deltaTime;
            controller.Move(velocity * Time.deltaTime);
            
            // 防止掉出地图
            if (transform.position.y < -10)
            {
                transform.position = new Vector3(0, 1, -25);
                velocity = Vector3.zero;
            }
        }
    }
    
    /// <summary>
    /// 相机跟随
    /// </summary>
    public class CameraFollow : MonoBehaviour
    {
        public Transform target;
        public Vector3 offset = new Vector3(0, 8, -10);
        
        void Update()
        {
            if (target == null) return;
            
            // 直接跟随目标，无延迟
            transform.position = target.position + offset;
            transform.LookAt(target.position + Vector3.up * 1.5f);
        }
    }
    
    /// <summary>
    /// NPC巡逻
    /// </summary>
    public class NPCPatrol : MonoBehaviour
    {
        public Vector3 centerPoint;
        public float patrolRadius = 10f;
        public float moveSpeed = 2f;
        public float waitTime = 2f;
        
        private Vector3 targetPoint;
        private float waitCounter;
        private bool isWaiting;
        
        void Start()
        {
            SetNewTarget();
        }
        
        void Update()
        {
            if (isWaiting)
            {
                waitCounter -= Time.deltaTime;
                if (waitCounter <= 0)
                {
                    isWaiting = false;
                    SetNewTarget();
                }
                return;
            }
            
            // 移向目标
            Vector3 direction = (targetPoint - transform.position).normalized;
            transform.position += direction * moveSpeed * Time.deltaTime;
            
            // 朝向移动方向
            if (direction != Vector3.zero)
            {
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, 
                    Quaternion.LookRotation(direction), 
                    Time.deltaTime * 3f);
            }
            
            // 检查是否到达
            if (Vector3.Distance(transform.position, targetPoint) < 1f)
            {
                isWaiting = true;
                waitCounter = waitTime + Random.Range(0, 2f);
            }
        }
        
        void SetNewTarget()
        {
            Vector2 randomCircle = Random.insideUnitCircle * patrolRadius;
            targetPoint = centerPoint + new Vector3(randomCircle.x, 0, randomCircle.y);
        }
    }
    
    /// <summary>
    /// 摩天轮旋转
    /// </summary>
    public class FerrisWheelRotator : MonoBehaviour
    {
        public float rotationSpeed = 5f;
        
        void Update()
        {
            // 旋转所有子对象（除中心和支架）
            foreach (Transform child in transform)
            {
                if (child.name.StartsWith("Cabin") || child.name.StartsWith("Spoke"))
                {
                    // 绕中心旋转
                    child.RotateAround(
                        transform.position + Vector3.up * 15,
                        Vector3.forward,
                        rotationSpeed * Time.deltaTime);
                }
            }
        }
    }
    
    /// <summary>
    /// 旋转木马旋转
    /// </summary>
    public class CarouselRotator : MonoBehaviour
    {
        public float rotationSpeed = 10f;
        
        void Update()
        {
            transform.Rotate(Vector3.up, rotationSpeed * Time.deltaTime);
        }
    }
    
    /// <summary>
    /// 过山车运动
    /// </summary>
    public class RollerCoasterMovement : MonoBehaviour
    {
        public Vector3[] trackPoints;
        public GameObject cart;
        
        public float speed = 10f;
        private int currentPoint = 0;
        
        void Update()
        {
            if (cart == null || trackPoints == null || trackPoints.Length == 0) return;
            
            // 移向下一个点
            Vector3 targetPos = trackPoints[currentPoint];
            cart.transform.localPosition = Vector3.MoveTowards(
                cart.transform.localPosition,
                targetPos,
                speed * Time.deltaTime);
            
            // 朝向
            Vector3 direction = targetPos - cart.transform.localPosition;
            if (direction != Vector3.zero)
            {
                cart.transform.rotation = Quaternion.LookRotation(direction);
            }
            
            // 检查到达
            if (Vector3.Distance(cart.transform.localPosition, targetPos) < 0.5f)
            {
                currentPoint = (currentPoint + 1) % trackPoints.Length;
            }
        }
    }
    
    /// <summary>
    /// 秋千动画
    /// </summary>
    public class SwingAnimator : MonoBehaviour
    {
        public float swingAngle = 30f;
        public float swingSpeed = 2f;
        public float delay = 0f;
        
        private float timer;
        
        void Update()
        {
            timer += Time.deltaTime;
            float angle = Mathf.Sin((timer + delay) * swingSpeed) * swingAngle;
            transform.localRotation = Quaternion.Euler(0, 0, angle);
        }
    }
}
