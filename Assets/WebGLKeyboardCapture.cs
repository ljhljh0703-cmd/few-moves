using System;
using UnityEngine;

namespace Nectorial.SlideEscape.Unity
{
    internal static class WebGLKeyboardCapture
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void DisableCanvasWideKeyboardCapture()
        {
            Type webGLInputType = Type.GetType("UnityEngine.WebGLInput, UnityEngine.WebGLModule", false);
            if (webGLInputType == null)
            {
                throw new InvalidOperationException(
                    "UnityEngine.WebGLInput was not found in UnityEngine.WebGLModule.");
            }

            var captureProperty = webGLInputType.GetProperty(
                "captureAllKeyboardInput",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (captureProperty == null || captureProperty.PropertyType != typeof(bool) ||
                !captureProperty.CanRead || !captureProperty.CanWrite)
            {
                throw new InvalidOperationException(
                    "WebGLInput.captureAllKeyboardInput is not a readable and writable static Boolean property.");
            }

            captureProperty.SetValue(null, false);
            object configuredValue = captureProperty.GetValue(null);
            if (!(configuredValue is bool) || (bool)configuredValue)
            {
                throw new InvalidOperationException(
                    "WebGLInput.captureAllKeyboardInput could not be set to false.");
            }
        }
#endif
    }
}
