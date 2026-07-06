using UnityEngine;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Pi.UnityHarness.Editor.Tests
{
    /// <summary>
    /// 乐园场景持久化加载器 - 在Play模式下保持场景
    /// </summary>
    [InitializeOnLoad]
    public static class AmusementParkScene
    {
        private static GameObject parkRoot;
        
        static AmusementParkScene()
        {
            // 注册Play模式状态变化事件
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }
        
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                // 进入Play模式时创建场景
                CreateParkScene();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                // 退出Play模式时清理
                CleanupParkScene();
            }
        }
        
        [MenuItem("Tools/Create Amusement Park")]
        public static void CreateParkScene()
        {
            // 清除现有场景
            CleanupParkScene();
            
            parkRoot = new GameObject("AmusementPark");
            
            CreateGround();
            CreateFences();
            CreateFerrisWheel();
            CreateCarousel();
            CreateRollerCoaster();
            CreateSwings();
            CreateTrees();
            CreateFlowerBeds();
            CreateLampPosts();
            CreateNPCs();
            CreatePlayer();
            CreateUI();
            
            Debug.Log("=== 欢乐游乐园创建完成 ===");
            Debug.Log("控制方式: WASD移动, 空格跳跃, 鼠标旋转");
        }
        
        private static void CleanupParkScene()
        {
            if (parkRoot != null)
            {
                Object.DestroyImmediate(parkRoot);
                parkRoot = null;
            }
        }
        
        private static void CreateGround()
        {
            // 主地面
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.SetParent(parkRoot.transform);
            ground.transform.position = new Vector3(0, -0.5f, 0);
            ground.transform.localScale = new Vector3(100, 1, 100);
            SetColor(ground, new Color(0.4f, 0.8f, 0.4f));
            
            // 道路
            CreatePath(new Vector3(0, 0.01f, 0), new Vector3(60, 0.02f, 3));
            CreatePath(new Vector3(0, 0.01f, 0), new Vector3(3, 0.02f, 60));
            CreatePath(new Vector3(20, 0.01f, 20), new Vector3(3, 0.02f, 30));
            CreatePath(new Vector3(-20, 0.01f, -20), new Vector3(30, 0.02f, 3));
        }
        
        private static void CreatePath(Vector3 pos, Vector3 scale)
        {
            var path = GameObject.CreatePrimitive(PrimitiveType.Cube);
            path.name = "Path";
            path.transform.SetParent(parkRoot.transform);
            path.transform.position = pos;
            path.transform.localScale = scale;
            SetColor(path, new Color(0.8f, 0.7f, 0.5f));
        }
        
        private static void CreateFences()
        {
            var fenceParent = new GameObject("Fences");
            fenceParent.transform.SetParent(parkRoot.transform);
            
            for (int i = 0; i < 4; i++)
            {
                var fence = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fence.name = "Fence_" + i;
                fence.transform.SetParent(fenceParent.transform);
                
                float xPos = (i % 2 == 0) ? (i == 0 ? -30 : 30) : 0;
                float zPos = (i % 2 == 1) ? (i == 1 ? -30 : 30) : 0;
                float xScale = (i % 2 == 1) ? 60 : 1;
                float zScale = (i % 2 == 0) ? 60 : 1;
                
                fence.transform.position = new Vector3(xPos, 1, zPos);
                fence.transform.localScale = new Vector3(xScale, 2, zScale);
                SetColor(fence, new Color(0.6f, 0.4f, 0.2f));
            }
            
            // 入口门
            var gate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gate.name = "Gate";
            gate.transform.SetParent(fenceParent.transform);
            gate.transform.position = new Vector3(0, 1.5f, -30);
            gate.transform.localScale = new Vector3(8, 3, 1);
            SetColor(gate, Color.red);
        }
        
        private static void CreateFerrisWheel()
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
            SetColor(center, Color.red);
            
            // 支架
            for (int i = 0; i < 2; i++)
            {
                var support = GameObject.CreatePrimitive(PrimitiveType.Cube);
                support.name = "Support_" + i;
                support.transform.SetParent(wheel.transform);
                support.transform.localPosition = new Vector3(i == 0 ? -2 : 2, 7.5f, 0);
                support.transform.localScale = new Vector3(0.5f, 15, 0.5f);
                support.transform.rotation = Quaternion.Euler(0, 0, i == 0 ? 15 : -15);
                SetColor(support, Color.gray);
            }
            
            // 轮辐和吊舱
            Color[] colors = { Color.red, Color.blue, Color.green, Color.yellow, Color.magenta, Color.cyan, Color.white, new Color(1f, 0.5f, 0f) };
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f * Mathf.Deg2Rad;
                float x = Mathf.Cos(angle) * 12;
                float y = Mathf.Sin(angle) * 12 + 15;
                
                var spoke = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                spoke.name = "Spoke_" + i;
                spoke.transform.SetParent(wheel.transform);
                spoke.transform.localPosition = new Vector3(x / 2, y / 2 + 7.5f, 0);
                spoke.transform.localScale = new Vector3(0.1f, 7, 0.1f);
                spoke.transform.rotation = Quaternion.Euler(0, 0, i * 45);
                SetColor(spoke, Color.white);
                
                var cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cabin.name = "Cabin_" + i;
                cabin.transform.SetParent(wheel.transform);
                cabin.transform.localPosition = new Vector3(x, y + 7.5f, 0);
                cabin.transform.localScale = new Vector3(2, 2, 2);
                SetColor(cabin, colors[i]);
            }
            
            // 添加旋转脚本
            wheel.AddComponent<FerrisWheelRotator>();
        }
        
        private static void CreateCarousel()
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
            SetColor(basePlatform, new Color(0.8f, 0.6f, 0.4f));
            
            // 中心柱
            var centerPole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            centerPole.name = "CenterPole";
            centerPole.transform.SetParent(carousel.transform);
            centerPole.transform.localPosition = new Vector3(0, 4, 0);
            centerPole.transform.localScale = new Vector3(1, 4, 1);
            SetColor(centerPole, Color.yellow);
            
            // 顶棚
            var roof = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            roof.name = "Roof";
            roof.transform.SetParent(carousel.transform);
            roof.transform.localPosition = new Vector3(0, 8, 0);
            roof.transform.localScale = new Vector3(13, 0.3f, 13);
            SetColor(roof, Color.red);
            
            // 木马
            Color[] horseColors = { Color.white, new Color(0.6f, 0.3f, 0.1f), Color.gray, new Color(0.8f, 0.8f, 0.6f) };
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f * Mathf.Deg2Rad;
                float x = Mathf.Cos(angle) * 5;
                float z = Mathf.Sin(angle) * 5;
                
                var horse = new GameObject("Horse_" + i);
                horse.transform.SetParent(carousel.transform);
                horse.transform.localPosition = new Vector3(x, 1.5f, z);
                horse.transform.rotation = Quaternion.Euler(0, -i * 45, 0);
                
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                body.transform.SetParent(horse.transform);
                body.transform.localPosition = Vector3.zero;
                body.transform.localScale = new Vector3(0.8f, 0.5f, 1.2f);
                SetColor(body, horseColors[i % horseColors.Length]);
                
                var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                head.name = "Head";
                head.transform.SetParent(horse.transform);
                head.transform.localPosition = new Vector3(0, 0.3f, 0.8f);
                head.transform.localScale = new Vector3(0.5f, 0.5f, 0.7f);
                head.GetComponent<Renderer>().material = body.GetComponent<Renderer>().material;
                
                var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pole.name = "Pole";
                pole.transform.SetParent(horse.transform);
                pole.transform.localPosition = new Vector3(0, 2.5f, 0);
                pole.transform.localScale = new Vector3(0.1f, 2.5f, 0.1f);
                SetColor(pole, Color.gray);
            }
            
            // 添加旋转脚本
            carousel.AddComponent<CarouselRotator>();
        }
        
        private static void CreateRollerCoaster()
        {
            var coaster = new GameObject("RollerCoaster");
            coaster.transform.SetParent(parkRoot.transform);
            coaster.transform.position = new Vector3(0, 0, -25);
            
            Vector3[] trackPoints = {
                new Vector3(0, 1, 0), new Vector3(5, 3, -5), new Vector3(10, 8, -10),
                new Vector3(15, 12, -15), new Vector3(20, 8, -20), new Vector3(25, 3, -15),
                new Vector3(30, 1, -10), new Vector3(25, 1, -5), new Vector3(20, 1, 0),
                new Vector3(15, 1, 5), new Vector3(10, 1, 0), new Vector3(5, 1, -2)
            };
            
            for (int i = 0; i < trackPoints.Length; i++)
            {
                var trackPiece = GameObject.CreatePrimitive(PrimitiveType.Cube);
                trackPiece.name = "Track_" + i;
                trackPiece.transform.SetParent(coaster.transform);
                trackPiece.transform.localPosition = trackPoints[i];
                
                Vector3 nextPoint = trackPoints[(i + 1) % trackPoints.Length];
                float distance = Vector3.Distance(trackPoints[i], nextPoint);
                trackPiece.transform.localScale = new Vector3(1, 0.3f, distance);
                trackPiece.transform.LookAt(coaster.transform.TransformPoint(nextPoint));
                SetColor(trackPiece, Color.red);
                
                if (trackPoints[i].y > 2)
                {
                    var support = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    support.name = "Support_" + i;
                    support.transform.SetParent(coaster.transform);
                    support.transform.localPosition = new Vector3(trackPoints[i].x, trackPoints[i].y / 2, trackPoints[i].z);
                    support.transform.localScale = new Vector3(0.3f, trackPoints[i].y / 2, 0.3f);
                    SetColor(support, Color.gray);
                }
            }
            
            // 过山车车辆
            var cart = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cart.name = "Cart";
            cart.transform.SetParent(coaster.transform);
            cart.transform.localPosition = trackPoints[0] + Vector3.up * 0.5f;
            cart.transform.localScale = new Vector3(1.5f, 1, 2);
            SetColor(cart, Color.blue);
            
            // 添加运动脚本
            var movement = coaster.AddComponent<RollerCoasterMovement>();
            movement.trackPoints = trackPoints;
            movement.cart = cart;
        }
        
        private static void CreateSwings()
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
            SetColor(topBar, Color.gray);
            
            for (int i = 0; i < 3; i++)
            {
                var swing = new GameObject("Swing_" + i);
                swing.transform.SetParent(swings.transform);
                swing.transform.localPosition = new Vector3(-3 + i * 3, 0, 0);
                
                var rope = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                rope.name = "Rope";
                rope.transform.SetParent(swing.transform);
                rope.transform.localPosition = new Vector3(0, 2.5f, 0);
                rope.transform.localScale = new Vector3(0.05f, 2.5f, 0.05f);
                SetColor(rope, new Color(0.6f, 0.4f, 0.2f));
                
                var seat = GameObject.CreatePrimitive(PrimitiveType.Cube);
                seat.name = "Seat";
                seat.transform.SetParent(swing.transform);
                seat.transform.localPosition = new Vector3(0, 0.2f, 0);
                seat.transform.localScale = new Vector3(1, 0.15f, 0.5f);
                SetColor(seat, new Color(0.4f, 0.25f, 0.1f));
                
                // 添加摆动脚本
                var swingAnim = swing.AddComponent<SwingAnimator>();
                swingAnim.delay = i * 0.5f;
            }
        }
        
        private static void CreateTrees()
        {
            var treeParent = new GameObject("Trees");
            treeParent.transform.SetParent(parkRoot.transform);
            
            Vector3[] positions = {
                new Vector3(-15, 0, 15), new Vector3(15, 0, -15), new Vector3(-35, 0, 0),
                new Vector3(35, 0, 0), new Vector3(0, 0, 35), new Vector3(0, 0, -35),
                new Vector3(-20, 0, -10), new Vector3(20, 0, 10), new Vector3(10, 0, 20),
                new Vector3(-10, 0, -20)
            };
            
            foreach (var pos in positions)
            {
                var tree = new GameObject("Tree");
                tree.transform.SetParent(treeParent.transform);
                tree.transform.position = pos;
                
                var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                trunk.name = "Trunk";
                trunk.transform.SetParent(tree.transform);
                trunk.transform.localPosition = new Vector3(0, 2, 0);
                trunk.transform.localScale = new Vector3(0.5f, 2, 0.5f);
                SetColor(trunk, new Color(0.4f, 0.25f, 0.1f));
                
                var crown = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                crown.name = "Crown";
                crown.transform.SetParent(tree.transform);
                crown.transform.localPosition = new Vector3(0, 4.5f, 0);
                crown.transform.localScale = new Vector3(3, 3, 3);
                SetColor(crown, new Color(0.1f, 0.6f, 0.1f));
            }
        }
        
        private static void CreateFlowerBeds()
        {
            var flowerParent = new GameObject("FlowerBeds");
            flowerParent.transform.SetParent(parkRoot.transform);
            
            Vector3[] positions = {
                new Vector3(-10, 0, 5), new Vector3(10, 0, -5),
                new Vector3(-5, 0, -15), new Vector3(5, 0, 15)
            };
            
            Color[] colors = { Color.red, Color.yellow, Color.magenta, Color.cyan, Color.white };
            
            foreach (var pos in positions)
            {
                var bed = new GameObject("FlowerBed");
                bed.transform.SetParent(flowerParent.transform);
                bed.transform.position = pos + Vector3.up * 0.3f;
                
                var baseObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                baseObj.name = "Base";
                baseObj.transform.SetParent(bed.transform);
                baseObj.transform.localScale = new Vector3(4, 0.6f, 4);
                SetColor(baseObj, new Color(0.5f, 0.3f, 0.2f));
                
                for (int i = 0; i < 8; i++)
                {
                    var flower = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    flower.name = "Flower";
                    flower.transform.SetParent(bed.transform);
                    flower.transform.localPosition = new Vector3(-1.5f + (i % 4) * 1f, 0.5f, -1.5f + (i / 4) * 3f);
                    flower.transform.localScale = Vector3.one * 0.3f;
                    SetColor(flower, colors[i % colors.Length]);
                }
            }
        }
        
        private static void CreateLampPosts()
        {
            var lampParent = new GameObject("LampPosts");
            lampParent.transform.SetParent(parkRoot.transform);
            
            Vector3[] positions = {
                new Vector3(-8, 0, 0), new Vector3(8, 0, 0), new Vector3(0, 0, -8),
                new Vector3(0, 0, 8), new Vector3(-15, 0, 15), new Vector3(15, 0, -15)
            };
            
            foreach (var pos in positions)
            {
                var lamp = new GameObject("LampPost");
                lamp.transform.SetParent(lampParent.transform);
                lamp.transform.position = pos;
                
                var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pole.name = "Pole";
                pole.transform.SetParent(lamp.transform);
                pole.transform.localPosition = new Vector3(0, 3, 0);
                pole.transform.localScale = new Vector3(0.2f, 3, 0.2f);
                SetColor(pole, Color.gray);
                
                var bulb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                bulb.name = "LightBulb";
                bulb.transform.SetParent(lamp.transform);
                bulb.transform.localPosition = new Vector3(0, 5.5f, 0);
                bulb.transform.localScale = Vector3.one * 0.8f;
                SetColor(bulb, Color.yellow);
                
                var light = bulb.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 15f;
                light.intensity = 0.8f;
                light.color = new Color(1f, 0.95f, 0.8f);
            }
            
            // 太阳光
            var sun = new GameObject("Sun");
            var sunLight = sun.AddComponent<Light>();
            sunLight.type = LightType.Directional;
            sunLight.color = new Color(1f, 0.95f, 0.85f);
            sunLight.intensity = 1f;
            sun.transform.rotation = Quaternion.Euler(50, -30, 0);
        }
        
        private static void CreateNPCs()
        {
            var npcParent = new GameObject("NPCs");
            npcParent.transform.SetParent(parkRoot.transform);
            
            string[] names = { "小明", "小红", "大叔", "阿姨", "小朋友" };
            Color[] colors = { Color.blue, Color.red, Color.green, Color.yellow, Color.cyan };
            Vector3[] positions = {
                new Vector3(10, 0, 10), new Vector3(-10, 0, -10), new Vector3(15, 0, -15),
                new Vector3(-15, 0, 15), new Vector3(0, 0, 20)
            };
            
            for (int i = 0; i < names.Length; i++)
            {
                var npc = new GameObject(names[i]);
                npc.transform.SetParent(npcParent.transform);
                npc.transform.position = positions[i];
                
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                body.transform.SetParent(npc.transform);
                body.transform.localPosition = new Vector3(0, 1, 0);
                SetColor(body, colors[i]);
                
                var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                head.name = "Head";
                head.transform.SetParent(npc.transform);
                head.transform.localPosition = new Vector3(0, 2, 0);
                head.transform.localScale = Vector3.one * 0.6f;
                SetColor(head, new Color(0.9f, 0.75f, 0.6f));
                
                // 添加巡逻脚本
                var patrol = npc.AddComponent<NPCPatrol>();
                patrol.centerPoint = positions[i];
                patrol.patrolRadius = 10f;
            }
        }
        
        private static void CreatePlayer()
        {
            var player = new GameObject("Player");
            player.transform.SetParent(parkRoot.transform);
            player.transform.position = new Vector3(0, 0, -25);
            
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(player.transform);
            body.transform.localPosition = new Vector3(0, 1, 0);
            SetColor(body, new Color(0.2f, 0.4f, 0.8f));
            
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(player.transform);
            head.transform.localPosition = new Vector3(0, 2, 0);
            head.transform.localScale = Vector3.one * 0.6f;
            SetColor(head, new Color(0.9f, 0.75f, 0.6f));
            
            var controller = player.AddComponent<CharacterController>();
            controller.center = new Vector3(0, 1, 0);
            controller.height = 2;
            controller.radius = 0.5f;
            
            player.AddComponent<PlayerControllerNewInput>();
            
            // 相机跟随
            var mainCam = Camera.main;
            if (mainCam != null)
            {
                var camFollow = mainCam.gameObject.AddComponent<CameraFollow>();
                camFollow.target = player.transform;
                camFollow.offset = new Vector3(0, 8, -10);
            }
        }
        
        private static void CreateUI()
        {
            var canvas = new GameObject("GameCanvas");
            var canvasComp = canvas.AddComponent<Canvas>();
            canvasComp.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.AddComponent<UnityEngine.UI.CanvasScaler>();
            canvas.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            
            CreateText(canvas.transform, "TitleText", "欢乐游乐园", new Vector2(0.3f, 0.9f), new Vector2(0.7f, 1f), 28, Color.white);
            CreateText(canvas.transform, "HelpText", "WASD: 移动 | 空格: 跳跃 | 鼠标: 旋转视角", new Vector2(0.2f, 0), new Vector2(0.8f, 0.08f), 16, Color.yellow);
        }
        
        private static void CreateText(Transform parent, string name, string text, Vector2 anchorMin, Vector2 anchorMax, int fontSize, Color color)
        {
            var textObj = new GameObject(name);
            textObj.transform.SetParent(parent);
            
            var rect = textObj.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            
            var textComp = textObj.AddComponent<UnityEngine.UI.Text>();
            textComp.text = text;
            textComp.fontSize = fontSize;
            textComp.color = color;
            textComp.alignment = TextAnchor.MiddleCenter;
            textComp.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }
        
        private static void SetColor(GameObject obj, Color color)
        {
            var renderer = obj.GetComponent<Renderer>();
            if (renderer != null)
            {
                var mat = new Material(Shader.Find("Standard"));
                mat.color = color;
                renderer.material = mat;
            }
        }
    }
    
    /// <summary>
    /// 兼容两种输入系统的玩家控制器
    /// </summary>
    public class PlayerControllerNewInput : MonoBehaviour
    {
        private CharacterController controller;
        private Vector3 velocity;
        private bool isGrounded;
        
        public float moveSpeed = 8f;
        public float jumpForce = 5f;
        public float gravity = -15f;
        public float mouseSensitivity = 2f;
        
        private float rotationX = 0f;
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
            
            // 锁定鼠标
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        
        void Update()
        {
            if (controller == null) return;
            
            isGrounded = controller.isGrounded;
            
            if (isGrounded && velocity.y < 0)
            {
                velocity.y = -2f;
            }
            
            float horizontal = 0f;
            float vertical = 0f;
            bool jumpPressed = false;
            float mouseX = 0f;
            float mouseY = 0f;
            
            // 尝试新 Input System
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) vertical += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) vertical -= 1f;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) horizontal -= 1f;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) horizontal += 1f;
                jumpPressed = keyboard.spaceKey.wasPressedThisFrame;
            }
            
            if (mouse != null)
            {
                mouseX = mouse.delta.x.ReadValue();
                mouseY = mouse.delta.y.ReadValue();
            }
            
            // 应用鼠标旋转
            rotationY += mouseX * mouseSensitivity;
            rotationX -= mouseY * mouseSensitivity;
            rotationX = Mathf.Clamp(rotationX, -80f, 80f);
            
            transform.rotation = Quaternion.Euler(rotationX, rotationY, 0);
            
            // 移动
            Vector3 move = transform.right * horizontal + transform.forward * vertical;
            move = move.normalized;
            controller.Move(move * moveSpeed * Time.deltaTime);
            
            // 跳跃
            if (jumpPressed && isGrounded)
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
        
        void OnDestroy()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
    
    /// <summary>
    /// 自动移动测试 - 验证移动功能
    /// </summary>
    public class AutoMoveTest : MonoBehaviour
    {
        public float speed = 5f;
        public float changeDirectionTime = 3f;
        private float timer = 0f;
        private Vector3 moveDirection;
        private CharacterController controller;
        private Vector3 startPosition;
        
        void Start()
        {
            controller = GetComponent<CharacterController>();
            startPosition = transform.position;
            moveDirection = transform.forward;
            Debug.Log("[AutoMoveTest] 开始自动移动测试");
        }
        
        void Update()
        {
            if (controller == null) return;
            
            timer += Time.deltaTime;
            if (timer >= changeDirectionTime)
            {
                timer = 0f;
                moveDirection = Quaternion.Euler(0, Random.Range(0, 360), 0) * Vector3.forward;
            }
            
            controller.Move(moveDirection * speed * Time.deltaTime);
            
            // 防止掉出地图
            if (transform.position.y < -10)
            {
                transform.position = startPosition + Vector3.up;
            }
        }
        
        void OnGUI()
        {
            GUI.Label(new Rect(10, 10, 300, 20), "自动移动测试中 - 位置: " + transform.position.ToString("F1"));
        }
    }
}
