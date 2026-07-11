using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class EditModeScreenshotTool
    {
        public static byte[] CaptureSceneViewJpg(int jpgQuality = 50, int maxWidth = 1280)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || sceneView.camera == null)
                return null;

            var cam = sceneView.camera;

            int width = maxWidth;
            int height = (int)(width * ((float)cam.pixelHeight / cam.pixelWidth));

            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var previousTarget = cam.targetTexture;
            var previousActive = RenderTexture.active;

            try
            {
                cam.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                cam.Render();

                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                var jpgBytes = tex.EncodeToJPG(jpgQuality);
                Object.DestroyImmediate(tex);
                return jpgBytes;
            }
            finally
            {
                cam.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }
        }
    }
}

