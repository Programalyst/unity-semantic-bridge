using UnityEngine;
using System.Collections;
using System;

namespace Gamenami.UnitySemanticBridge
{
    public class ScreenshotTool : HiddenSingleton<ScreenshotTool>
    {
        [Range(0, 100)]
        public int jpgQuality = 50;

        public void GetScreenshotBytes(Action<byte[]> onCompleteCallback) 
        {
            StartCoroutine(CaptureScreenshotBytes(onCompleteCallback));
        }

        private IEnumerator CaptureScreenshotBytes(Action<byte[]> onCompleteCallback) 
        {
            yield return new WaitForEndOfFrame();
            
            // 1280x720 enough for Gemini to see tiles
            var width = Screen.width > 1280 ? 1280 : Screen.width;
            var height = (int)(width * ((float)Screen.height / Screen.width));
            var screenshotTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            
            screenshotTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            screenshotTexture.Apply();
            
            var jpgBytes = screenshotTexture.EncodeToJPG(jpgQuality);

            Destroy(screenshotTexture);
            
            onCompleteCallback?.Invoke(jpgBytes);
        }
    }
}