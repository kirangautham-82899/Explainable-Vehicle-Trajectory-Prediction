using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class TrainDataCollection : MonoBehaviour
{

    private StreamWriter writer;
    private string filePath;
    private string fileName = "Train_Data.txt";
    private Rigidbody rb;
    private float speed;

    void Start()
    {
        filePath = System.IO.Path.Combine(Application.dataPath, fileName);
        writer = new StreamWriter(filePath, true);
        rb = GetComponent<Rigidbody>();
    }

    void FixedUpdate()
    {
        Vector3 pos = transform.position;
        Quaternion rot = transform.rotation;
        speed = rb.linearVelocity.magnitude;

        writer.WriteLine($"{speed},{pos.x},{pos.y},{pos.z},{rot.x},{rot.y},{rot.z}");
    }

    void OnApplicationQuit()
    {
        writer.Close();
    }
}



