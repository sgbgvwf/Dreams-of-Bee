using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraRotation : MonoBehaviour
{
    void Update()
    {
        transform.RotateAround(transform.position, Vector3.up, 0.01f);
    }
}
