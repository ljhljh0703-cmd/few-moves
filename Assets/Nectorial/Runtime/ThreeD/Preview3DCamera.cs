using UnityEngine;

namespace Nectorial.SlideEscape.Unity
{
    // Fixed orthographic camera for the 3D preview: tilted toward the board, no orbit or input.
    // Yaw 0 and roll 0 keep Up on screen-up and Right on screen-right.
    internal static class Preview3DCamera
    {
        // Low enough to show wall and puck sides, high enough that no cell is hidden behind a wall.
        public const float PitchDegrees = 59f;
        // The canvas is square, so width decides: an 8-cell board plus tray rim is 8.7 wide, plus a safe margin.
        public const float OrthographicSize = 4.7f;
        private const float Distance = 20f;

        public static void Configure(Camera camera)
        {
            camera.orthographic = true;
            camera.orthographicSize = OrthographicSize;
            camera.transform.rotation = Quaternion.Euler(PitchDegrees, 0f, 0f);
            camera.transform.position = camera.transform.rotation * new Vector3(0f, 0f, -Distance);
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = Distance * 2f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color32(0xf0, 0xec, 0xe2, 0xff);
        }
    }
}
