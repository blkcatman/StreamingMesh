using UnityEngine;

namespace StreamingMesh.Samples
{
    /// <summary>Runtime view adjustment shared by the receiver sample players.</summary>
    public sealed class ReceiverCameraControls : MonoBehaviour
    {
        public Camera viewCamera;
        [Min(0.1f)] public float positionRange = 20;
        public bool IsOpen { get; private set; }

        Transform cameraTransform;
        Vector3 initialPosition;
        Quaternion initialRotation;
        float x, y, z, yawOffset;
        string xLabel, yLabel, zLabel, yawLabel;
        static readonly GUILayoutOption[] ButtonHeight = { GUILayout.Height(32) };
        static readonly GUILayoutOption[] StepWidth = { GUILayout.Width(52), GUILayout.Height(32) };

        void Start() => EnsureCamera();

        bool EnsureCamera()
        {
            Camera camera = viewCamera != null ? viewCamera : Camera.main;
            if (camera == null) return false;
            if (cameraTransform == camera.transform) return true;
            cameraTransform = camera.transform;
            initialPosition = cameraTransform.position;
            initialRotation = cameraTransform.rotation;
            x = initialPosition.x;
            y = initialPosition.y;
            z = initialPosition.z;
            yawOffset = 0;
            UpdateLabels();
            return true;
        }

        public void SetPositionXZ(float worldX, float worldZ)
        {
            if (!EnsureCamera() || !IsFinite(worldX) || !IsFinite(worldZ)) return;
            float range = Mathf.Max(0.1f, positionRange);
            x = Mathf.Clamp(worldX, initialPosition.x - range, initialPosition.x + range);
            z = Mathf.Clamp(worldZ, initialPosition.z - range, initialPosition.z + range);
            ApplyView();
        }

        public void SetYawOffset(float degrees)
        {
            if (!EnsureCamera() || !IsFinite(degrees)) return;
            yawOffset = Mathf.Clamp(degrees, -180, 180);
            ApplyView();
        }

        public void SetPositionY(float worldY)
        {
            if (!EnsureCamera() || !IsFinite(worldY)) return;
            float range = Mathf.Max(0.1f, positionRange);
            y = Mathf.Clamp(worldY, initialPosition.y - range, initialPosition.y + range);
            ApplyView();
        }

        public void ResetView()
        {
            if (!EnsureCamera()) return;
            x = initialPosition.x;
            y = initialPosition.y;
            z = initialPosition.z;
            yawOffset = 0;
            ApplyView();
        }

        void ApplyView()
        {
            // Keep the scene's tilt. Pre-multiplication rotates about
            // world Y even when the camera has a pitched initial orientation.
            cameraTransform.SetPositionAndRotation(new Vector3(x, y, z),
                Quaternion.AngleAxis(yawOffset, Vector3.up) * initialRotation);
            UpdateLabels();
        }

        void UpdateLabels()
        {
            // Format only after a change, rather than on every IMGUI repaint.
            xLabel = $"X: {x:F2} m";
            yLabel = $"Y (height): {y:F2} m";
            zLabel = $"Z: {z:F2} m";
            yawLabel = $"Yaw (Y): {Mathf.Repeat(initialRotation.eulerAngles.y + yawOffset, 360):F1}°";
        }

        public void DrawToggle()
        {
            if (GUILayout.Button(IsOpen ? "Back to playback" : "Camera view", ButtonHeight))
                IsOpen = !IsOpen;
        }

        public void DrawPanel()
        {
            if (!EnsureCamera())
            {
                GUILayout.Label("No receiver camera found.");
                return;
            }
            float range = Mathf.Max(0.1f, positionRange);
            GUILayout.Label(xLabel);
            GUILayout.BeginHorizontal();
            float nextX = x;
            if (GUILayout.Button("-0.1 m", StepWidth)) nextX -= 0.1f;
            nextX = GUILayout.HorizontalSlider(nextX, initialPosition.x - range, initialPosition.x + range);
            if (GUILayout.Button("+0.1 m", StepWidth)) nextX += 0.1f;
            GUILayout.EndHorizontal();
            if (nextX != x) SetPositionXZ(nextX, z);

            GUILayout.Label(yLabel);
            GUILayout.BeginHorizontal();
            float nextY = y;
            if (GUILayout.Button("-0.1 m", StepWidth)) nextY -= 0.1f;
            nextY = GUILayout.HorizontalSlider(nextY, initialPosition.y - range, initialPosition.y + range);
            if (GUILayout.Button("+0.1 m", StepWidth)) nextY += 0.1f;
            GUILayout.EndHorizontal();
            if (nextY != y) SetPositionY(nextY);

            GUILayout.Label(zLabel);
            GUILayout.BeginHorizontal();
            float nextZ = z;
            if (GUILayout.Button("-0.1 m", StepWidth)) nextZ -= 0.1f;
            nextZ = GUILayout.HorizontalSlider(nextZ, initialPosition.z - range, initialPosition.z + range);
            if (GUILayout.Button("+0.1 m", StepWidth)) nextZ += 0.1f;
            GUILayout.EndHorizontal();
            if (nextZ != z) SetPositionXZ(x, nextZ);

            GUILayout.Label(yawLabel);
            GUILayout.BeginHorizontal();
            float nextYaw = yawOffset;
            if (GUILayout.Button("-5°", StepWidth)) nextYaw -= 5;
            nextYaw = GUILayout.HorizontalSlider(nextYaw, -180, 180);
            if (GUILayout.Button("+5°", StepWidth)) nextYaw += 5;
            GUILayout.EndHorizontal();
            if (nextYaw != yawOffset) SetYawOffset(nextYaw);
            if (GUILayout.Button("Reset camera", ButtonHeight)) ResetView();
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
