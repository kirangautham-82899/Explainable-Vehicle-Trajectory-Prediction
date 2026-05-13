using System.Collections;
using System.IO;
using UnityEngine;

public class DataCollection_1 : MonoBehaviour
{

    private StreamWriter writer;
    private string filePath;
    private string fileName = "AI_model/Input_Data.txt";


    private string screenshotFolder;
    public float intervalSeconds = 0.5f; // Time between screenshots

    void Start()
    {
        filePath = System.IO.Path.Combine(Application.dataPath, fileName);
        writer = new StreamWriter(filePath, true);


        // Create a folder in persistent data path if it doesn't exist
        screenshotFolder = System.IO.Path.Combine(Application.dataPath, "AI_model/Screenshots");
        if (!Directory.Exists(screenshotFolder))
        {
            Directory.CreateDirectory(screenshotFolder);
        }

        // Start the automatic screenshot coroutine
        StartCoroutine(CaptureScreenshots());
    }

    private IEnumerator CaptureScreenshots()
    {
        while (true)
        {
            TakeScreenshot();
            yield return new WaitForSeconds(intervalSeconds);
        }
    }

    private void TakeScreenshot()
    {
        try
        {
            string fileName = "Screenshot_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png";
            string filePath = System.IO.Path.Combine(screenshotFolder, fileName);

            ScreenCapture.CaptureScreenshot(filePath);


            Debug.Log($"Screenshot saved to: {filePath}");

            writer.WriteLine($"{Input.GetKey(KeyCode.W)},{Input.GetKey(KeyCode.A)},{Input.GetKey(KeyCode.S)},{Input.GetKey(KeyCode.D)},{Input.GetKey(KeyCode.Space)},{System.DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError("Failed to take screenshot: " + ex.Message);
        }
    }

    void OnApplicationQuit()
    {
        writer.Close();
    }

}
