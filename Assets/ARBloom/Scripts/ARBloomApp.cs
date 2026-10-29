using System;
using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace HakoLab.ARBloom
{
    /// <summary>
    /// One-component AR prototype:
    /// - creates AR Session + XR Origin at runtime
    /// - scans horizontal planes with ARCore
    /// - center-screen reticle raycasts to the floor
    /// - hold the LOWER half of the screen and physically sweep/rotate the phone
    /// - flowers paint under the reticle and auto-fill inward so the result is a filled bloom, not a donut
    /// </summary>
    public sealed class ARBloomApp : MonoBehaviour
    {
        [Header("Bloom")]
        [SerializeField, Range(80, 900)] private int maxFlowers = 450;
        [SerializeField, Range(5f, 30f)] private float flowersPerSquareMeter = 16f;
        [SerializeField, Range(0.05f, 0.25f)] private float fillStep = 0.12f;
        [SerializeField, Range(0.05f, 0.35f)] private float brushRadius = 0.18f;
        [SerializeField, Range(0.04f, 0.18f)] private float minimumFlowerSpacing = 0.085f;
        [SerializeField, Range(0.7f, 3f)] private float growthSpeed = 1.8f;
        [SerializeField, Range(0.8f, 4f)] private float maxBloomRadius = 2.6f;

        private ARSession arSession;
        private ARPlaneManager planeManager;
        private ARRaycastManager raycastManager;
        private Camera arCamera;
        private GameObject reticle;
        private Renderer reticleRenderer;

        private Mesh flowerMesh;
        private Material flowerMaterial;
        private Material reticleMaterial;
        private readonly List<ARRaycastHit> raycastHits = new List<ARRaycastHit>(8);
        private readonly List<FlowerRecord> activeFlowers = new List<FlowerRecord>(512);
        private readonly Stack<GameObject> flowerPool = new Stack<GameObject>(128);
        private readonly HashSet<long> occupiedCells = new HashSet<long>();

        private bool hasSurface;
        private Pose surfacePose;
        private bool blooming;
        private bool hasBloomCenter;
        private Vector3 bloomCenter;
        private float maxFilledRadius;
        private Vector3 lastBrushPosition;
        private bool hasLastBrushPosition;
        private string statusText = "Menyiapkan AR…";
        private GUIStyle titleStyle;
        private GUIStyle bodyStyle;
        private GUIStyle statusStyle;
        private GUIStyle buttonStyle;
        private Texture2D panelTexture;

        private readonly Color[] flowerTints =
        {
            new Color(1.00f, 0.63f, 0.78f, 1f),
            new Color(0.93f, 0.68f, 1.00f, 1f),
            new Color(1.00f, 0.83f, 0.47f, 1f),
            new Color(0.74f, 0.88f, 1.00f, 1f),
            new Color(1.00f, 0.74f, 0.62f, 1f),
        };

        private struct FlowerRecord
        {
            public Transform transform;
            public float bornAt;
            public float targetScale;
            public float phase;
        }

        private void Awake()
        {
            Application.targetFrameRate = 60;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            Input.multiTouchEnabled = false;

            SetupAR();
            SetupAssets();
            SetupReticle();
            StartCoroutine(CheckARAvailability());
        }

        private void OnDestroy()
        {
            ARSession.stateChanged -= OnARStateChanged;
            if (flowerMaterial != null) Destroy(flowerMaterial);
            if (reticleMaterial != null) Destroy(reticleMaterial);
            if (flowerMesh != null) Destroy(flowerMesh);
            if (panelTexture != null) Destroy(panelTexture);
        }

        private void SetupAR()
        {
            var sessionGo = new GameObject("AR Session");
            arSession = sessionGo.AddComponent<ARSession>();
            arSession.attemptUpdate = true;
            sessionGo.AddComponent<ARInputManager>();

            var originGo = new GameObject("XR Origin (Mobile AR)");
            var xrOrigin = originGo.AddComponent<XROrigin>();

            var cameraOffset = new GameObject("Camera Offset");
            cameraOffset.transform.SetParent(originGo.transform, false);

            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            cameraGo.transform.SetParent(cameraOffset.transform, false);

            arCamera = cameraGo.AddComponent<Camera>();
            arCamera.nearClipPlane = 0.01f;
            arCamera.farClipPlane = 30f;
            arCamera.clearFlags = CameraClearFlags.SolidColor;
            arCamera.backgroundColor = Color.black;

            cameraGo.AddComponent<ARCameraManager>();
            cameraGo.AddComponent<ARCameraBackground>();
#pragma warning disable 0618
            cameraGo.AddComponent<ARPoseDriver>();
#pragma warning restore 0618

            xrOrigin.Camera = arCamera;
            xrOrigin.CameraFloorOffsetObject = cameraOffset;
            xrOrigin.CameraYOffset = 0f;
            xrOrigin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;

            planeManager = originGo.AddComponent<ARPlaneManager>();
            planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;

            raycastManager = originGo.AddComponent<ARRaycastManager>();

            ARSession.stateChanged += OnARStateChanged;
        }

        private void SetupAssets()
        {
            var shader = Resources.Load<Shader>("ARBloomVertexColor");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");

            flowerMaterial = new Material(shader) { name = "ARBloom Flower Runtime Material" };
            reticleMaterial = new Material(shader) { name = "ARBloom Reticle Runtime Material" };
            reticleMaterial.SetColor("_Tint", new Color(1f, 0.9f, 0.35f, 1f));
            flowerMesh = BuildFlowerMesh();
        }

        private void SetupReticle()
        {
            reticle = new GameObject("Bloom Reticle");
            var mf = reticle.AddComponent<MeshFilter>();
            reticleRenderer = reticle.AddComponent<MeshRenderer>();
            mf.sharedMesh = BuildRingMesh(0.085f, 0.105f, 48);
            reticleRenderer.sharedMaterial = reticleMaterial;
            reticle.SetActive(false);
        }

        private IEnumerator CheckARAvailability()
        {
            statusText = "Mengecek dukungan ARCore…";
            yield return ARSession.CheckAvailability();

            if (ARSession.state == ARSessionState.NeedsInstall)
            {
                statusText = "Memasang / memperbarui Google Play Services for AR…";
                yield return ARSession.Install();
            }

            RefreshStatusFromState(ARSession.state);
        }

        private void OnARStateChanged(ARSessionStateChangedEventArgs args)
        {
            RefreshStatusFromState(args.state);
        }

        private void RefreshStatusFromState(ARSessionState state)
        {
            switch (state)
            {
                case ARSessionState.Unsupported:
                    statusText = "ARCore tidak didukung oleh device ini.";
                    break;
                case ARSessionState.CheckingAvailability:
                    statusText = "Mengecek dukungan ARCore…";
                    break;
                case ARSessionState.NeedsInstall:
                    statusText = "Google Play Services for AR perlu diinstal / diupdate.";
                    break;
                case ARSessionState.Installing:
                    statusText = "Memasang layanan AR…";
                    break;
                case ARSessionState.Ready:
                case ARSessionState.SessionInitializing:
                    statusText = "Arahkan kamera ke lantai dan gerakkan HP pelan-pelan.";
                    break;
                case ARSessionState.SessionTracking:
                    statusText = "Scan lantai sampai reticle kuning muncul.";
                    break;
                default:
                    statusText = "Menyiapkan AR…";
                    break;
            }
        }

        private void Update()
        {
            UpdateRaycastAndReticle();
            UpdateInput();
            AnimateFlowers();
            AnimateReticle();
        }

        private void UpdateRaycastAndReticle()
        {
            hasSurface = false;
            if (raycastManager == null || arCamera == null)
                return;

            Vector2 screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            if (raycastManager.Raycast(screenCenter, raycastHits, TrackableType.PlaneWithinPolygon) && raycastHits.Count > 0)
            {
                surfacePose = raycastHits[0].pose;
                hasSurface = true;

                reticle.SetActive(true);
                reticle.transform.SetPositionAndRotation(
                    surfacePose.position + surfacePose.up * 0.004f,
                    Quaternion.FromToRotation(Vector3.up, surfacePose.up));

                if (!blooming)
                    statusText = "Tahan bagian BAWAH layar, lalu gerakkan / putar HP. Reticle = kuas bunga.";
            }
            else
            {
                reticle.SetActive(false);
                if (ARSession.state == ARSessionState.SessionTracking)
                    statusText = "Belum nemu lantai. Gerakkan HP pelan kiri-kanan sambil arahkan ke lantai.";
            }
        }

        private void UpdateInput()
        {
            bool hold = false;

#if UNITY_EDITOR
            hold = Input.GetMouseButton(0);
#else
            if (Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                // Lower 48% keeps the finger away from the reticle in the screen center.
                hold = touch.position.y < Screen.height * 0.48f &&
                       touch.phase != TouchPhase.Ended &&
                       touch.phase != TouchPhase.Canceled;
            }
#endif

            if (hold && hasSurface && ARSession.state == ARSessionState.SessionTracking)
            {
                if (!blooming)
                    BeginBloom(surfacePose.position);

                PaintAt(surfacePose.position, surfacePose.up);
                blooming = true;
                statusText = $"BLOOM  {activeFlowers.Count}/{maxFlowers}  •  sapu reticle makin luas";
            }
            else
            {
                blooming = false;
                hasLastBrushPosition = false;
            }
        }

        private void BeginBloom(Vector3 worldPoint)
        {
            if (!hasBloomCenter)
            {
                bloomCenter = worldPoint;
                hasBloomCenter = true;
                maxFilledRadius = 0f;
                SpawnBurst(worldPoint, Vector3.up, 7, 0.11f);
            }
        }

        private void PaintAt(Vector3 hitPoint, Vector3 surfaceNormal)
        {
            if (activeFlowers.Count >= maxFlowers)
                return;

            if (!hasLastBrushPosition || Vector3.Distance(lastBrushPosition, hitPoint) >= minimumFlowerSpacing * 0.7f)
            {
                SpawnBurst(hitPoint, surfaceNormal, 2, brushRadius);
                lastBrushPosition = hitPoint;
                hasLastBrushPosition = true;
            }

            float radius = HorizontalDistance(bloomCenter, hitPoint);
            radius = Mathf.Min(radius, maxBloomRadius);

            // Critical bit: whenever the sweep reaches farther out, fill the whole new annulus.
            // The result becomes a filled disk/garden instead of a hollow donut.
            while (maxFilledRadius + fillStep <= radius && activeFlowers.Count < maxFlowers)
            {
                float inner = maxFilledRadius;
                float outer = Mathf.Min(maxFilledRadius + fillStep, maxBloomRadius);
                FillAnnulus(inner, outer, surfaceNormal);
                maxFilledRadius = outer;
            }
        }

        private void FillAnnulus(float innerRadius, float outerRadius, Vector3 surfaceNormal)
        {
            float area = Mathf.PI * (outerRadius * outerRadius - innerRadius * innerRadius);
            int count = Mathf.Clamp(Mathf.CeilToInt(area * flowersPerSquareMeter), 3, 26);

            for (int i = 0; i < count && activeFlowers.Count < maxFlowers; i++)
            {
                float t = UnityEngine.Random.value;
                float r = Mathf.Sqrt(Mathf.Lerp(innerRadius * innerRadius, outerRadius * outerRadius, t));
                float a = UnityEngine.Random.value * Mathf.PI * 2f;
                Vector3 p = bloomCenter + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                SpawnFlowerIfFree(p, surfaceNormal);
            }
        }

        private void SpawnBurst(Vector3 center, Vector3 surfaceNormal, int count, float radius)
        {
            for (int i = 0; i < count && activeFlowers.Count < maxFlowers; i++)
            {
                Vector2 rnd = UnityEngine.Random.insideUnitCircle * radius;
                Vector3 p = center + new Vector3(rnd.x, 0f, rnd.y);
                SpawnFlowerIfFree(p, surfaceNormal);
            }
        }

        private void SpawnFlowerIfFree(Vector3 position, Vector3 surfaceNormal)
        {
            long key = SpatialKey(position, minimumFlowerSpacing);
            if (!occupiedCells.Add(key))
                return;

            GameObject go;
            if (flowerPool.Count > 0)
            {
                go = flowerPool.Pop();
                go.SetActive(true);
            }
            else
            {
                go = new GameObject("Flower");
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mf.sharedMesh = flowerMesh;
                mr.sharedMaterial = flowerMaterial;
            }

            position += surfaceNormal.normalized * 0.0035f;
            go.transform.position = position;
            go.transform.rotation = Quaternion.FromToRotation(Vector3.up, surfaceNormal) *
                                    Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);

            float targetScale = UnityEngine.Random.Range(0.72f, 1.25f);
            go.transform.localScale = Vector3.one * 0.01f;

            var renderer = go.GetComponent<MeshRenderer>();
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            Color tint = flowerTints[UnityEngine.Random.Range(0, flowerTints.Length)];
            tint *= UnityEngine.Random.Range(0.92f, 1.08f);
            tint.a = 1f;
            block.SetColor("_Tint", tint);
            renderer.SetPropertyBlock(block);

            activeFlowers.Add(new FlowerRecord
            {
                transform = go.transform,
                bornAt = Time.time,
                targetScale = targetScale,
                phase = UnityEngine.Random.Range(0f, Mathf.PI * 2f)
            });
        }

        private void AnimateFlowers()
        {
            float now = Time.time;
            for (int i = 0; i < activeFlowers.Count; i++)
            {
                FlowerRecord record = activeFlowers[i];
                if (record.transform == null)
                    continue;

                float age = (now - record.bornAt) * growthSpeed;
                float grow = Mathf.Clamp01(age);
                grow = 1f - Mathf.Pow(1f - grow, 3f);

                float breathe = age > 1f ? 1f + Mathf.Sin(now * 1.7f + record.phase) * 0.018f : 1f;
                record.transform.localScale = Vector3.one * (record.targetScale * grow * breathe);
            }
        }

        private void AnimateReticle()
        {
            if (reticle == null || !reticle.activeSelf)
                return;

            float pulse = 1f + Mathf.Sin(Time.time * 5f) * 0.10f;
            reticle.transform.localScale = Vector3.one * pulse;
        }

        public void ResetBloom()
        {
            for (int i = 0; i < activeFlowers.Count; i++)
            {
                Transform t = activeFlowers[i].transform;
                if (t == null) continue;
                t.gameObject.SetActive(false);
                flowerPool.Push(t.gameObject);
            }

            activeFlowers.Clear();
            occupiedCells.Clear();
            hasBloomCenter = false;
            maxFilledRadius = 0f;
            hasLastBrushPosition = false;
            blooming = false;
            statusText = "Reset. Scan lantai lalu mulai bloom lagi.";
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            Vector2 aa = new Vector2(a.x, a.z);
            Vector2 bb = new Vector2(b.x, b.z);
            return Vector2.Distance(aa, bb);
        }

        private static long SpatialKey(Vector3 p, float cellSize)
        {
            int x = Mathf.RoundToInt(p.x / cellSize);
            int z = Mathf.RoundToInt(p.z / cellSize);
            unchecked
            {
                return ((long)x << 32) ^ (uint)z;
            }
        }

        private Mesh BuildFlowerMesh()
        {
            var vertices = new List<Vector3>(96);
            var colors = new List<Color>(96);
            var triangles = new List<int>(192);

            Color stem = new Color(0.20f, 0.62f, 0.28f, 1f);
            Color leaf = new Color(0.25f, 0.72f, 0.32f, 1f);
            Color petal = Color.white;
            Color center = new Color(1.0f, 0.72f, 0.10f, 1f);

            float h = 0.16f;
            float w = 0.012f;

            // Crossed stem quads.
            AddQuad(vertices, colors, triangles,
                new Vector3(-w, 0, 0), new Vector3(w, 0, 0), new Vector3(w, h, 0), new Vector3(-w, h, 0), stem);
            AddQuad(vertices, colors, triangles,
                new Vector3(0, 0, -w), new Vector3(0, 0, w), new Vector3(0, h, w), new Vector3(0, h, -w), stem);

            // Leaves.
            AddDiamond(vertices, colors, triangles, new Vector3(0, h * 0.45f, 0), new Vector3(0.065f, 0.018f, 0.02f), 30f, leaf);
            AddDiamond(vertices, colors, triangles, new Vector3(0, h * 0.62f, 0), new Vector3(0.055f, 0.016f, 0.018f), 210f, leaf);

            // Five petals around the top.
            Vector3 flowerCenter = new Vector3(0, h, 0);
            for (int i = 0; i < 5; i++)
            {
                float angle = i * 72f;
                float rad = angle * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Cos(rad), 0.10f, Mathf.Sin(rad)).normalized;
                Vector3 side = new Vector3(-dir.z, 0, dir.x);
                Vector3 c = flowerCenter + dir * 0.035f;
                Vector3 tip = flowerCenter + dir * 0.085f + Vector3.up * 0.012f;
                Vector3 left = c - side * 0.032f;
                Vector3 right = c + side * 0.032f;
                Vector3 inner = flowerCenter + Vector3.up * 0.006f;
                AddQuad(vertices, colors, triangles, inner, right, tip, left, petal);
            }

            // Yellow center, crossed so it reads from several angles.
            AddQuad(vertices, colors, triangles,
                flowerCenter + new Vector3(-0.025f, 0.010f, 0),
                flowerCenter + new Vector3(0.025f, 0.010f, 0),
                flowerCenter + new Vector3(0.025f, 0.045f, 0),
                flowerCenter + new Vector3(-0.025f, 0.045f, 0), center);
            AddQuad(vertices, colors, triangles,
                flowerCenter + new Vector3(0, 0.010f, -0.025f),
                flowerCenter + new Vector3(0, 0.010f, 0.025f),
                flowerCenter + new Vector3(0, 0.045f, 0.025f),
                flowerCenter + new Vector3(0, 0.045f, -0.025f), center);

            var mesh = new Mesh { name = "ARBloom Low Poly Flower" };
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddDiamond(List<Vector3> v, List<Color> c, List<int> t, Vector3 center, Vector3 size, float yaw, Color color)
        {
            Quaternion rot = Quaternion.Euler(0, yaw, 0);
            Vector3 forward = rot * Vector3.forward;
            Vector3 right = rot * Vector3.right;

            Vector3 a = center - right * size.x * 0.2f;
            Vector3 b = center + forward * size.x + Vector3.up * size.y;
            Vector3 d = center - forward * size.x * 0.15f - Vector3.up * size.z;
            Vector3 cc = center + right * size.x * 0.2f;
            AddQuad(v, c, t, a, b, cc, d, color);
        }

        private static void AddQuad(List<Vector3> v, List<Color> c, List<int> t,
            Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Color color)
        {
            int start = v.Count;
            v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
            c.Add(color); c.Add(color); c.Add(color); c.Add(color);

            // Front
            t.Add(start + 0); t.Add(start + 1); t.Add(start + 2);
            t.Add(start + 0); t.Add(start + 2); t.Add(start + 3);
            // Back (shader already Cull Off, but doubled triangles keep normal-dependent fallback sane)
            t.Add(start + 2); t.Add(start + 1); t.Add(start + 0);
            t.Add(start + 3); t.Add(start + 2); t.Add(start + 0);
        }

        private static Mesh BuildRingMesh(float innerRadius, float outerRadius, int segments)
        {
            var vertices = new Vector3[segments * 2];
            var colors = new Color[vertices.Length];
            var triangles = new int[segments * 6];

            for (int i = 0; i < segments; i++)
            {
                float a = (i / (float)segments) * Mathf.PI * 2f;
                float x = Mathf.Cos(a);
                float z = Mathf.Sin(a);
                vertices[i * 2] = new Vector3(x * innerRadius, 0, z * innerRadius);
                vertices[i * 2 + 1] = new Vector3(x * outerRadius, 0, z * outerRadius);
                colors[i * 2] = Color.white;
                colors[i * 2 + 1] = Color.white;
            }

            int tri = 0;
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                int i0 = i * 2;
                int i1 = i * 2 + 1;
                int n0 = next * 2;
                int n1 = next * 2 + 1;

                triangles[tri++] = i0;
                triangles[tri++] = i1;
                triangles[tri++] = n1;
                triangles[tri++] = i0;
                triangles[tri++] = n1;
                triangles[tri++] = n0;
            }

            var mesh = new Mesh { name = "ARBloom Reticle Ring" };
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void EnsureGUIStyles()
        {
            if (titleStyle != null)
                return;

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(Screen.height * 0.028f),
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                wordWrap = true
            };

            bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(Screen.height * 0.020f),
                normal = { textColor = new Color(0.95f, 0.95f, 0.95f) },
                wordWrap = true
            };

            statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(Screen.height * 0.019f),
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.88f, 0.38f) },
                wordWrap = true
            };

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(Screen.height * 0.018f),
                fontStyle = FontStyle.Bold
            };

            panelTexture = new Texture2D(1, 1);
            panelTexture.SetPixel(0, 0, new Color(0, 0, 0, 0.62f));
            panelTexture.Apply();
        }

        private void OnGUI()
        {
            EnsureGUIStyles();

            float margin = Screen.width * 0.045f;
            float width = Screen.width - margin * 2f;
            float panelHeight = Screen.height * 0.19f;
            Rect panel = new Rect(margin, margin, width, panelHeight);

            Color old = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(panel, panelTexture, ScaleMode.StretchToFill);

            float x = panel.x + panel.width * 0.04f;
            float y = panel.y + panel.height * 0.10f;
            float contentWidth = panel.width * 0.72f;

            GUI.Label(new Rect(x, y, contentWidth, panel.height * 0.28f), "AR BLOOM", titleStyle);
            GUI.Label(new Rect(x, y + panel.height * 0.28f, contentWidth, panel.height * 0.26f), statusText, statusStyle);
            GUI.Label(new Rect(x, y + panel.height * 0.58f, contentWidth, panel.height * 0.30f),
                "Tahan bawah layar → arahkan reticle → gerakkan HP melingkar/menyapu. Makin jauh = area otomatis terisi, bukan donat.", bodyStyle);

            Rect resetRect = new Rect(panel.x + panel.width * 0.79f, panel.y + panel.height * 0.18f,
                panel.width * 0.17f, panel.height * 0.45f);
            if (GUI.Button(resetRect, "RESET", buttonStyle))
                ResetBloom();

            if (ARSession.state == ARSessionState.Unsupported)
            {
                Rect warning = new Rect(margin, Screen.height * 0.74f, width, Screen.height * 0.12f);
                GUI.DrawTexture(warning, panelTexture, ScaleMode.StretchToFill);
                GUI.Label(new Rect(warning.x + 20, warning.y + 10, warning.width - 40, warning.height - 20),
                    "Device melaporkan ARCore UNSUPPORTED. Kalau Redmi 14C-mu punya Google Play Services for AR tapi tetap mentok di sini, berarti profil ARCore device-nya belum mengizinkan sesi tracking.", bodyStyle);
            }

            GUI.color = old;
        }
    }
}
