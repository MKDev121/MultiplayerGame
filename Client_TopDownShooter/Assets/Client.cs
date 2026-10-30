using UnityEngine;
using UnityEngine.Rendering;
using System.Net;
using System.Net.Sockets;
using UnityEngine.UI;
using System.Text;
using System.Threading.Tasks;
using MyExternalLibrary;
using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.ComponentModel.Design.Serialization;
using System.Linq;
using Unity.VisualScripting;



enum ConnectionState
{
    Connecting,
    Connected
}

public class Client : MonoBehaviour
{
    UdpClient client;
    public string remoteIPAddress;
    ConnectionState connectionState;
    public GameObject messageBox;
    public GameObject playerObject;
    private Transform gunPos;
    private LineRenderer aimRayRenderer;
    public float shootRange = 100f;
    public InputField inputField;
    public Transform[] spawnLocations;

    readonly ConcurrentQueue<PlayerData[]> snapshots = new ConcurrentQueue<PlayerData[]>();
    readonly ConcurrentQueue<string> controlMessages = new ConcurrentQueue<string>();
    IPEndPoint remoteIP;
    int clientID;
    bool connectedToServer = false;
    public GameObject[] playerObjects;
    bool moving;
    public Text interpolationStatus;
    public RectTransform playerHealthBarFill;
    private RectTransform[] opponentHealthBarFills;
    int tick = 0;

    int inputSequence = 1;
    PlayerData latestPlayerSnapshot;
    PlayerData latestOpponentSnapshot;
    bool hasLatestPlayerSnapshot;
    int latestServerTick = -1;

    // Project Chronos: Quantum Splinter & Retrospective Anti-Cheat fields
    [Header("Project Chronos - Quantum Splinter")]
    public Volume postProcessVolume;
    public Text chronosAlertText;
    public Image chronosGlitchOverlay;
    public Text cheatToggleButtonText;

    private bool isSplinterActive;
    private float splinterElapsed;
    private const float SplinterDuration = 5.0f;
    private const int SplinterLedgerLength = 60;
    private int splinterStartServerTick;
    private TimelineLedgerTick[] splinterLedgerBuffer;
    private int lastAppliedSplinterTick = -1;
    private float realignmentFlashTimer;
    private bool injectCheatDrift;
    private bool isSessionBanned;
    private string telemetryFilePath;
    private float heartbeatTimer;
    private const float HeartbeatInterval = 0.1f;

    void Start()
    {
        remoteIP = new IPEndPoint(IPAddress.Loopback, 11_000);
        client = new UdpClient();
        InitializeTelemetryLog();
        UpdateChronosVisuals();
        Debug.Log("This is executed just after Connect To Server");
    }

    private void InitializeTelemetryLog()
    {
        try
        {
            string dir = Path.Combine(Application.dataPath, "TelemetryLogs");
            Directory.CreateDirectory(dir);
            telemetryFilePath = Path.Combine(dir, "live_telemetry.csv");
            if (!File.Exists(telemetryFilePath))
            {
                File.WriteAllText(
                    telemetryFilePath,
                    "Distance_To_Enemy,Enemy_Visible,Player_HP,Player_Ammo,In_Chokepoint,Intent_Label,Vector_X_To_Enemy,Vector_Y_To_Enemy,Move_Input_X,Move_Input_Y,Trigger_Shoot\n");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Chronos Telemetry] Could not initialize telemetry file: {ex.Message}");
        }
    }

    public void TriggerQuantumSplinter()
    {
        if (!connectedToServer || isSplinterActive || isSessionBanned || !hasLatestPlayerSnapshot)
            return;

        isSplinterActive = true;
        splinterElapsed = 0f;
        lastAppliedSplinterTick = -1;
        splinterStartServerTick = latestServerTick;

        int opponentId = clientID == 0 ? 1 : 0;
        float oppX = latestOpponentSnapshot.x;
        float oppY = latestOpponentSnapshot.y;
        float oppHp = latestOpponentSnapshot.health > 0 ? latestOpponentSnapshot.health : 100f;
        if (playerObjects != null && opponentId < playerObjects.Length && playerObjects[opponentId] != null)
        {
            oppX = playerObjects[opponentId].transform.position.x;
            oppY = playerObjects[opponentId].transform.position.z;
        }
        // Activate the opponent player's personalized local model (zero network transfer overhead)
        ChronosModel.ActivatePlayerModel(opponentId);
        splinterLedgerBuffer = ChronosModel.GenerateDeterministicLedger(
            splinterStartServerTick,
            SplinterLedgerLength,
            latestOpponentSnapshot.x,
            latestOpponentSnapshot.y,
            oppHp,
            latestPlayerSnapshot.x,
            latestPlayerSnapshot.y,
            playerId: opponentId);

        if (injectCheatDrift && splinterLedgerBuffer.Length > 10)
        {
            // Phase 7: Cheat emulation test - manually inject a corrupted coordinate drift into RAM array buffer
            TimelineLedgerTick corrupted = splinterLedgerBuffer[10];
            corrupted.mx += 0.35f;
            splinterLedgerBuffer[10] = corrupted;
            Debug.LogWarning("[Chronos Cheat Test] Injected +0.35f coordinate drift into client RAM ledger buffer at index 10!");
        }

        Debug.Log($"[Chronos] Entered QUANTUM SPLINTER at serverTick={splinterStartServerTick} for {SplinterDuration:F1}s | Activated Model: {ChronosModel.GetModelProfileName(opponentId)} (player_{opponentId}).");
    }

    public void ToggleCheatInjection()
    {
        injectCheatDrift = !injectCheatDrift;
        if (cheatToggleButtonText != null)
        {
            cheatToggleButtonText.text = injectCheatDrift ? "Cheat Drift: ON (C)" : "Cheat Drift: OFF (C)";
        }
        Debug.Log($"[Chronos] Cheat Drift Emulation set to: {injectCheatDrift}");
    }

    void Update()
    {
        while (controlMessages.TryDequeue(out string ctrlMsg))
        {
            HandleChronosControlMessage(ctrlMsg);
        }

        if (Input.GetKeyDown(KeyCode.L))
        {
            TriggerQuantumSplinter();
        }
        if (Input.GetKeyDown(KeyCode.C))
        {
            ToggleCheatInjection();
        }

        if (isSessionBanned)
        {
            interpolationStatus.text = "SESSION BANNED";
            UpdateChronosVisuals();
            return;
        }

        interpolationStatus.text = isSplinterActive
            ? "SPLINTER: AI BRAIN"
            : "Interpolation: " + LagCompensation.canInterpolate.ToString();

        bool shotTriggeredThisFrame = false;
        if (connectedToServer && Input.GetMouseButtonDown(0))
        {
            if (!hasLatestPlayerSnapshot)
            {
                Debug.Log("[Shoot] Ignored: waiting for first server snapshot.");
                return;
            }

            shotTriggeredThisFrame = true;
            Transform originTransform = gunPos != null ? gunPos : transform;
            Vector3 rayDirection = originTransform.forward;
            ShootingClientPacket packet = new ShootingClientPacket
            {
                targetPlayerId = -1,
                observedServerTick = latestServerTick,
                baseInputSequence = latestPlayerSnapshot.lastProcessedInput,
                inputCount = 0,
                x = rayDirection.x,
                y = rayDirection.y,
                z = rayDirection.z
            };

            if (!isSplinterActive)
            {
                byte[] data = DataPacketConvertor.getBytes(packet, 1);
                client.Send(data, data.Length, remoteIP);
            }
            Debug.Log($"[Shoot] Sent shot (tick={packet.observedServerTick}, dir={rayDirection})");
        }

        if (connectedToServer)
        {
            if (gunPos != null)
            {
                Debug.DrawRay(gunPos.position, gunPos.forward * shootRange, Color.red);
                if (aimRayRenderer != null)
                {
                    aimRayRenderer.SetPosition(0, gunPos.position);
                    aimRayRenderer.SetPosition(1, gunPos.position + gunPos.forward * shootRange);
                }
            }

            float horizontal = Input.GetAxis("Horizontal");
            float vertical = Input.GetAxis("Vertical");
            if (Mathf.Abs(horizontal) > 0.01f || Mathf.Abs(vertical) > 0.01f)
            {
                MovementClientPacket packet = new MovementClientPacket();
                packet.deltaX = horizontal;
                packet.deltaY = vertical;
                packet.inputDuration = Time.deltaTime;
                packet.sequence = inputSequence;

                if (!isSplinterActive)
                {
                    byte[] data = DataPacketConvertor.getBytes(packet, 0);
                    client.Send(data, data.Length, remoteIP);
                }
                moving = true;

                LagCompensation.ClientSidePrediction(packet, playerObjects[clientID]);
                inputSequence++;
            }
            else
            {
                moving = false;
            }

            if (isSplinterActive)
            {
                UpdateQuantumSplinterSimulation(Time.deltaTime);
            }
            else
            {
                heartbeatTimer += Time.deltaTime;
                if (heartbeatTimer >= HeartbeatInterval)
                {
                    heartbeatTimer = 0f;
                    HeartbeatClientPacket hb = new HeartbeatClientPacket { clientTick = tick };
                    byte[] hbData = DataPacketConvertor.getBytes(hb, 3);
                    client.Send(hbData, hbData.Length, remoteIP);
                }
            }

            if (realignmentFlashTimer > 0f)
            {
                realignmentFlashTimer = Mathf.Max(0f, realignmentFlashTimer - Time.deltaTime);
            }

            RecordTelemetrySample(horizontal, vertical, shotTriggeredThisFrame ? 1 : 0);
        }

        UpdateChronosVisuals();
    }

    private void UpdateQuantumSplinterSimulation(float dt)
    {
        splinterElapsed += dt;
        int opponentId = clientID == 0 ? 1 : 0;
        if (playerObjects != null &&
            opponentId < playerObjects.Length &&
            playerObjects[opponentId] != null &&
            splinterLedgerBuffer != null &&
            splinterLedgerBuffer.Length > 0)
        {
            float normalizedTime = Mathf.Clamp01(splinterElapsed / SplinterDuration);
            int ledgerIdx = Mathf.Clamp(
                Mathf.FloorToInt(normalizedTime * (splinterLedgerBuffer.Length - 1)),
                0,
                splinterLedgerBuffer.Length - 1);

            TimelineLedgerTick currentTick = splinterLedgerBuffer[ledgerIdx];
            Vector3 moveDelta = new Vector3(currentTick.mx, 0f, currentTick.my) * dt * 6f;
            playerObjects[opponentId].transform.position += moveDelta;

            if (ledgerIdx != lastAppliedSplinterTick)
            {
                lastAppliedSplinterTick = ledgerIdx;
            }
        }

        if (splinterElapsed >= SplinterDuration)
        {
            CompleteQuantumSplinterAndSendHandshake();
        }
    }

    private void CompleteQuantumSplinterAndSendHandshake()
    {
        isSplinterActive = false;
        heartbeatTimer = 0f;
        LagCompensation.inputHistory.Clear();
        ChronosReconnectionHeader header = new ChronosReconnectionHeader
        {
            startServerTick = splinterStartServerTick,
            tickCount = splinterLedgerBuffer != null ? splinterLedgerBuffer.Length : 0
        };

        byte[] payload = DataPacketConvertor.getChronosReconnectionBytes(header, splinterLedgerBuffer, 2);
        client.Send(payload, payload.Length, remoteIP);
        Debug.Log($"[Chronos] Sent Reconnection Audit Payload ({payload.Length} bytes, {header.tickCount} ticks) to Server.");
    }

    private void HandleChronosControlMessage(string message)
    {
        // Format: CHRONOS:<VERDICT>:<REASON>:<ACTION>
        string[] parts = message.Split(':');
        if (parts.Length < 4)
            return;

        string verdict = parts[1];
        string reason = parts[2];
        string action = parts[3];

        if (verdict == "VERIFIED")
        {
            realignmentFlashTimer = 1.5f;
            Debug.Log($"[Chronos] Server Handshake VERIFIED ({action}). Reality Realigned!");
        }
        else
        {
            isSessionBanned = true;
            connectedToServer = false;
            Debug.LogError($"[Chronos] Server Handshake BANNED! Reason: {reason} ({action}). Connection terminated.");
        }
    }

    private void UpdateChronosVisuals()
    {
        if (isSessionBanned)
        {
            if (chronosAlertText != null)
            {
                chronosAlertText.text = "ANTI-CHEAT BANNED: INPUT DRIFT DETECTED — SESSION TERMINATED";
                chronosAlertText.color = new Color(1f, 0.25f, 0.25f, 1f);
            }
            if (chronosGlitchOverlay != null)
            {
                chronosGlitchOverlay.color = new Color(0.7f, 0.05f, 0.05f, 0.35f);
            }
            return;
        }

        if (isSplinterActive)
        {
            float remaining = Mathf.Max(0f, SplinterDuration - splinterElapsed);
            int opponentId = clientID == 0 ? 1 : 0;
            MacroIntent currentIntent = MacroIntent.AggressiveFlank;
            if (splinterLedgerBuffer != null && lastAppliedSplinterTick >= 0 && lastAppliedSplinterTick < splinterLedgerBuffer.Length)
            {
                currentIntent = (MacroIntent)splinterLedgerBuffer[lastAppliedSplinterTick].intent;
            }

            if (chronosAlertText != null)
            {
                chronosAlertText.text = $"QUANTUM SPLINTER ({remaining:F1}s) | Model: {ChronosModel.GetModelProfileName(opponentId)} | Intent: {currentIntent}";
                chronosAlertText.color = new Color(0.2f, 0.95f, 1f, 1f);
            }
            if (chronosGlitchOverlay != null)
            {
                float pulse = 0.12f + 0.06f * Mathf.Sin(Time.time * 12f);
                chronosGlitchOverlay.color = new Color(0.1f, 0.8f, 1f, pulse);
            }
            if (postProcessVolume != null)
            {
                postProcessVolume.weight = 1f;
            }
        }
        else if (realignmentFlashTimer > 0f)
        {
            if (chronosAlertText != null)
            {
                chronosAlertText.text = "REALITY REALIGNED — SYNC LINK RESTORED";
                chronosAlertText.color = new Color(0.4f, 1f, 0.5f, 1f);
            }
            if (chronosGlitchOverlay != null)
            {
                float alpha = Mathf.Clamp01(realignmentFlashTimer / 1.5f) * 0.28f;
                chronosGlitchOverlay.color = new Color(0.85f, 0.95f, 1f, alpha);
            }
        }
        else
        {
            if (chronosAlertText != null)
            {
                chronosAlertText.text = connectedToServer
                    ? $"SYNC LINK STABLE [My Model: {ChronosModel.GetModelProfileName(clientID)}] | [L] 5s Splinter  [C] Cheat"
                    : "";
                chronosAlertText.color = new Color(0.85f, 0.9f, 0.95f, 0.85f);
            }
            if (chronosGlitchOverlay != null)
            {
                chronosGlitchOverlay.color = new Color(0f, 0f, 0f, 0f);
            }
        }
    }

    private void RecordTelemetrySample(float moveX, float moveY, int shootTriggered)
    {
        if (string.IsNullOrEmpty(telemetryFilePath) || tick % 30 != 0 || playerObjects == null)
            return;

        int opponentId = clientID == 0 ? 1 : 0;
        if (clientID >= playerObjects.Length || opponentId >= playerObjects.Length ||
            playerObjects[clientID] == null || playerObjects[opponentId] == null)
            return;

        Vector3 myPos = playerObjects[clientID].transform.position;
        Vector3 enemyPos = playerObjects[opponentId].transform.position;
        float vx = enemyPos.x - myPos.x;
        float vy = enemyPos.z - myPos.z;
        float dist = Mathf.Sqrt(vx * vx + vy * vy);
        if (dist < 0.20f)
            return; // Skip unseparated spawn-overlap ticks

        int visible = dist <= 18f ? 1 : 0;
        float hp = latestPlayerSnapshot.health > 0 ? latestPlayerSnapshot.health : 100f;
        float ammo = 15f;
        int chokepoint = (Mathf.Abs(myPos.x) < 2.5f && Mathf.Abs(myPos.z) < 2.5f) ? 1 : 0;
        int intent = (int)ChronosModel.InferBehaviorIntent(moveX, moveY, vx, vy, hp, ammo, chokepoint);

        try
        {
            string line = $"{dist:F4},{visible},{hp:F1},{ammo:F1},{chokepoint},{intent},{vx:F4},{vy:F4},{moveX:F4},{moveY:F4},{shootTriggered},{clientID}\n";
            File.AppendAllText(telemetryFilePath, line);

            string playerFile = Path.Combine(Path.GetDirectoryName(telemetryFilePath), $"player_{clientID}_telemetry.csv");
            if (!File.Exists(playerFile))
            {
                File.WriteAllText(
                    playerFile,
                    "Player_ID,Distance_To_Enemy,Enemy_Visible,Player_HP,Player_Ammo,In_Chokepoint,Intent_Label,Vector_X_To_Enemy,Vector_Y_To_Enemy,Move_Input_X,Move_Input_Y,Trigger_Shoot\n");
            }
            string playerLine = $"{clientID},{dist:F4},{visible},{hp:F1},{ammo:F1},{chokepoint},{intent},{vx:F4},{vy:F4},{moveX:F4},{moveY:F4},{shootTriggered}\n";
            File.AppendAllText(playerFile, playerLine);
        }
        catch
        {
            // Ignore transient IO locks during gameplay
        }
    }

    void FixedUpdate()
    {
        if (isSessionBanned)
            return;

        while (snapshots.TryDequeue(out PlayerData[] snapshot))
        {
            if (isSplinterActive)
            {
                // Drop incoming server packets during simulated Quantum Splinter dropout
                continue;
            }

            hasLatestPlayerSnapshot = LagCompensation.UpdateStates(
                snapshot,
                ref latestPlayerSnapshot,
                ref latestServerTick,
                clientID) || hasLatestPlayerSnapshot;

            if (snapshot != null)
            {
                foreach (PlayerData pd in snapshot)
                {
                    float healthRatio = Mathf.Clamp01(pd.health / 100f);
                    if (pd.id == clientID)
                    {
                        if (playerHealthBarFill != null)
                            playerHealthBarFill.localScale = new Vector3(healthRatio, 1f, 1f);
                    }
                    else
                    {
                        latestOpponentSnapshot = pd;
                        if (opponentHealthBarFills != null &&
                            pd.id >= 0 &&
                            pd.id < opponentHealthBarFills.Length &&
                            opponentHealthBarFills[pd.id] != null)
                        {
                            opponentHealthBarFills[pd.id].localScale = new Vector3(healthRatio, 1f, 1f);
                        }
                    }
                }
            }
        }

        if (!isSplinterActive)
        {
            if (hasLatestPlayerSnapshot && playerObjects != null && clientID < playerObjects.Length && playerObjects[clientID] != null)
            {
                LagCompensation.ServerReconciliation(latestPlayerSnapshot, playerObjects[clientID]);
            }
            if (playerObjects != null)
            {
                LagCompensation.LinearInterpolate(playerObjects, clientID);
            }
        }
        tick++;
    }

    public void SendCNFMessage(string message)
    {
        byte[] data = Encoding.UTF8.GetBytes(message);
        client.Send(data, data.Length, remoteIP);
    }

    public void JoinServer(string ip)
    {
        remoteIP.Address = IPAddress.Parse(ip);
        ConnectToServer();
    }

    async Task ConnectToServer()
    {
        Debug.Log(remoteIP.ToString());

        while (true)
        {
            string request = "REQ";
            byte[] data = Encoding.UTF8.GetBytes(request);
            client.Send(data, data.Length, remoteIP);
            var result = await client.ReceiveAsync();
            string acknowledgement = Encoding.UTF8.GetString(result.Buffer, 0, result.Buffer.Length);
            if (acknowledgement.Contains("ACK"))
            {
                clientID = acknowledgement[acknowledgement.Length - 1] - '0';
                SendCNFMessage("CNF");
            }
            else if (acknowledgement.Contains("Start"))
            {
                connectionState = ConnectionState.Connected;
                messageBox.SetActive(true);
                playerObjects = new GameObject[2];
                opponentHealthBarFills = new RectTransform[2];

                //Instantiate Players
                for (int i = 0; i < 2; i++)
                {
                    GameObject obj = Instantiate(playerObject, spawnLocations[i].position, Quaternion.identity);
                    playerObjects[i] = obj;
                    playerObjects[i].GetComponentInChildren<Camera>().enabled = false;
                    Transform healthBar = obj.transform.Find("HealthBar");
                    if (healthBar != null)
                    {
                        if (i == clientID)
                        {
                            healthBar.gameObject.SetActive(false);
                        }
                        else
                        {
                            Transform fill = healthBar.Find("Fill");
                            if (fill != null)
                                opponentHealthBarFills[i] = fill.GetComponent<RectTransform>();
                        }
                    }
                }
                GetComponent<Camera>().enabled = false;
                playerObjects[clientID].GetComponentInChildren<Camera>().enabled = true;
                gunPos = playerObjects[clientID].transform.Find("GunPos");
                if (gunPos != null)
                {
                    aimRayRenderer = gunPos.GetComponent<LineRenderer>();
                    if (aimRayRenderer == null)
                        aimRayRenderer = gunPos.gameObject.AddComponent<LineRenderer>();
                    aimRayRenderer.positionCount = 2;
                    aimRayRenderer.startWidth = 0.05f;
                    aimRayRenderer.endWidth = 0.05f;
                    aimRayRenderer.useWorldSpace = true;
                    aimRayRenderer.material = new Material(Shader.Find("Sprites/Default"));
                    aimRayRenderer.startColor = Color.red;
                    aimRayRenderer.endColor = Color.red;
                }
                SendCNFMessage("ACK");
                connectedToServer = true;
                break;
            }
        }
        ListenToServer();
    }

    async Task ListenToServer()
    {
        while (client != null)
        {
            UdpReceiveResult message;
            try
            {
                message = await client.ReceiveAsync();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            int playerSize = Marshal.SizeOf<PlayerData>();
            if (message.Buffer.Length == playerSize * 2)
            {
                PlayerData[] snapshot = DataPacketConvertor.getAllPlayersData<PlayerData>(message.Buffer);
                snapshots.Enqueue(snapshot);
            }
            else if (message.Buffer.Length > 0)
            {
                string text = Encoding.UTF8.GetString(message.Buffer, 0, message.Buffer.Length);
                if (text.StartsWith("CHRONOS:"))
                {
                    controlMessages.Enqueue(text);
                }
            }
        }
    }

    void OnDestroy()
    {
        if (client != null)
        {
            try
            {
                client.Close();
                client.Dispose();
            }
            catch
            {
            }
            client = null;
        }
    }
}
