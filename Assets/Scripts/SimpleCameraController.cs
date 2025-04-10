using UnityEngine;

public class SimpleCameraController : MonoBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float moveSpeed = 5.0f;
    [SerializeField] private float fastMoveMultiplier = 2.0f;
    [SerializeField] private float verticalSpeed = 3.0f; // Speed for Q/E

    [Header("Rotation Settings")]
    [SerializeField] private float mouseSensitivity = 2.0f;

    [Header("State")]
    [SerializeField] private bool lockCursor = true; // Lock cursor when active?

    private float rotationX = 0.0f;
    private float rotationY = 0.0f;
    private bool isEnabled = false; // Control activation

    void Start()
    {
        // Store initial rotation
        Vector3 angles = transform.eulerAngles;
        rotationX = angles.y; // Yaw
        rotationY = angles.x; // Pitch

        // Ensure initial state is inactive visually
        if (lockCursor) Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        this.enabled = false; // Start disabled script-wise
    }

    public void SetActive(bool active)
    {
        isEnabled = active;
        this.enabled = active; // Enable/disable Update loop

        if (active)
        {
            if (lockCursor) Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = !lockCursor;
            // Re-grab current rotation when activating
            Vector3 angles = transform.eulerAngles;
            rotationX = angles.y;
            rotationY = angles.x;
        }
        else
        {
            if (lockCursor) Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }


    void Update()
    {
        if (!isEnabled) return; // Don't process if not explicitly activated

        // Rotation (Mouse Look)
        rotationX += Input.GetAxis("Mouse X") * mouseSensitivity;
        rotationY -= Input.GetAxis("Mouse Y") * mouseSensitivity;
        rotationY = Mathf.Clamp(rotationY, -90f, 90f); // Clamp vertical rotation

        transform.localRotation = Quaternion.Euler(rotationY, rotationX, 0);

        // Movement (WASD + Q/E)
        float currentMoveSpeed = Input.GetKey(KeyCode.LeftShift) ? moveSpeed * fastMoveMultiplier : moveSpeed;
        float moveForwardBack = Input.GetAxis("Vertical") * currentMoveSpeed * Time.deltaTime;
        float moveLeftRight = Input.GetAxis("Horizontal") * currentMoveSpeed * Time.deltaTime;

        transform.Translate(Vector3.forward * moveForwardBack, Space.Self);
        transform.Translate(Vector3.right * moveLeftRight, Space.Self);

        // Vertical Movement (Q/E)
        if (Input.GetKey(KeyCode.E))
        {
            transform.Translate(Vector3.up * verticalSpeed * Time.deltaTime, Space.World);
        }
        if (Input.GetKey(KeyCode.Q))
        {
            transform.Translate(Vector3.down * verticalSpeed * Time.deltaTime, Space.World);
        }
    }
}