using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using AgOpenNtripCaster.Server.Data;
using AgOpenNtripCaster.Server.Hubs;
using AgOpenNtripCaster.Server.Models.DTOs;
using AgOpenNtripCaster.Server.Models.Entities;
using AgOpenNtripCaster.Server.Services.Auth;
using AgOpenNtripCaster.Server.Services.Diagnostics;
using AgOpenNtripCaster.Server.Services.Notifications;
using AgOpenNtripCaster.Server.Services.Email;

namespace AgOpenNtripCaster.Server.Services.NTRIP;

/// <summary>
/// NTRIP Server core service
/// Listens on port 2101 for:
/// - GNSS source connections (push RTCM data)
/// - NTRIP client connections (pull RTCM data)
/// - Sourcetable requests (GET /)
/// </summary>
public class NtripServerService : IHostedService
{
    private readonly ILogger<NtripServerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConnectionPool _connectionPool;
    private readonly IHubContext<NtripHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ITelegramNotificationService _telegramService;
    private readonly Dictionary<string, string> _clientSessionIds; // clientId -> sessionId mapping
    private readonly ConcurrentDictionary<string, RoverDiagnosticCounters> _roverCounters; // clientId -> diagnostic counters
    private readonly ConcurrentDictionary<string, int> _sourceConnectionIds; // sourceId -> source connection id
    private readonly ConcurrentDictionary<string, SourceDiagnosticCounters> _sourceCounters; // sourceId -> diagnostic counters
    private readonly Dictionary<string, int> _mountPointClientCounts; // mountPointName -> client count

    private TcpListener? _tcpListener;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _acceptTask;
    private Timer? _statsUpdateTimer;
    private Timer? _healthCheckTimer;

    private readonly int _ntripPort;
    private readonly ConnectionAdmission _admission;
    private const int ListenBacklog = 128;
    private const int StatsUpdateIntervalMs = 10000; // Update stats every 10 seconds
    private const int HealthCheckIntervalMs = 10000; // Check client health every 10 seconds
    private const int StaleConnectionTimeoutSeconds = 30; // Disconnect clients after 30 seconds of inactivity
    private static readonly DateTime _serverStartTime = DateTime.UtcNow;

    public NtripServerService(
        ILogger<NtripServerService> logger,
        IServiceProvider serviceProvider,
        ConnectionPool connectionPool,
        IHubContext<NtripHub> hubContext,
        IConfiguration configuration,
        ITelegramNotificationService telegramService)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _connectionPool = connectionPool;
        _hubContext = hubContext;
        _configuration = configuration;
        _telegramService = telegramService;
        _clientSessionIds = new Dictionary<string, string>();
        _roverCounters = new ConcurrentDictionary<string, RoverDiagnosticCounters>();
        _sourceConnectionIds = new ConcurrentDictionary<string, int>();
        _sourceCounters = new ConcurrentDictionary<string, SourceDiagnosticCounters>();
        _mountPointClientCounts = new Dictionary<string, int>();

        // Read NTRIP port from configuration, default to 2101
        _ntripPort = configuration.GetValue<int>("NTRIP_PORT", 2101);
        _admission = new ConnectionAdmission(
            Math.Clamp(configuration.GetValue<int>("NTRIP_MAX_CONNECTIONS", 256), 1, 10000),
            Math.Clamp(configuration.GetValue<int>("NTRIP_MAX_CONNECTIONS_PER_IP", 32), 1, 1000));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting NTRIP Server on port {Port}...", _ntripPort);

            // Clean up orphaned ClientSessions on startup
            // All previous sessions are invalid since server just restarted
            await CleanupOrphanedSessionsAsync(cancellationToken);

            _tcpListener = new TcpListener(IPAddress.Any, _ntripPort);
            _tcpListener.Start(ListenBacklog);

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _acceptTask = AcceptConnectionsAsync(_cancellationTokenSource.Token);

            _logger.LogInformation("NTRIP Server started successfully on port {Port}", _ntripPort);

            // Start periodic stats broadcast timer (every 10 seconds for uptime updates)
            _statsUpdateTimer = new Timer(
                async _ => await BroadcastDashboardStatsAsync(CancellationToken.None),
                null,
                StatsUpdateIntervalMs,
                StatsUpdateIntervalMs);

            // Start health check timer (every 10 seconds to detect stale connections)
            _healthCheckTimer = new Timer(
                async _ => await PerformClientHealthCheckAsync(CancellationToken.None),
                null,
                HealthCheckIntervalMs,
                HealthCheckIntervalMs);

            // Start connection statistics collection (every second)
            var statsService = _serviceProvider.GetRequiredService<IConnectionStatsService>();
            await statsService.StartCollectionAsync();

            // Send Telegram notification that system has started
            await _telegramService.SendSystemStartedAsync(cancellationToken);

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start NTRIP Server");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Stopping NTRIP Server...");

            // Stop stats broadcast timer
            if (_statsUpdateTimer != null)
            {
                await _statsUpdateTimer.DisposeAsync();
            }

            // Stop health check timer
            if (_healthCheckTimer != null)
            {
                await _healthCheckTimer.DisposeAsync();
            }

            // Stop connection statistics collection
            try
            {
                var statsService = _serviceProvider.GetRequiredService<IConnectionStatsService>();
                await statsService.StopCollectionAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping connection statistics collection");
            }

            _tcpListener?.Stop();
            _cancellationTokenSource?.Cancel();

            if (_acceptTask != null)
            {
                await _acceptTask;
            }

            _logger.LogInformation("NTRIP Server stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping NTRIP Server");
        }
    }

    /// <summary>
    /// Clean up orphaned ClientSessions and SourceConnections on server startup
    /// Any session/connection without DisconnectedAt is invalid after server restart
    /// </summary>
    private async Task CleanupOrphanedSessionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var now = DateTime.UtcNow;

            // Clean up orphaned ClientSessions (DisconnectedAt == null)
            var orphanedSessions = await dbContext.ClientSessions
                .Where(cs => cs.DisconnectedAt == null)
                .ToListAsync(cancellationToken);

            if (orphanedSessions.Count > 0)
            {
                _logger.LogWarning("🧹 Cleaning up {Count} orphaned ClientSessions from previous server run", orphanedSessions.Count);

                foreach (var session in orphanedSessions)
                {
                    session.DisconnectedAt = now;
                    session.Status = ClientStreamStatus.Disconnected;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogWarning("🧹 ClientSessions cleanup complete! Marked {Count} sessions as disconnected", orphanedSessions.Count);
            }
            else
            {
                _logger.LogInformation("✅ No orphaned ClientSessions to clean up");
            }

            // Clean up orphaned SourceConnections (DisconnectedAt == null)
            var orphanedConnections = await dbContext.SourceConnections
                .Where(sc => sc.DisconnectedAt == null)
                .ToListAsync(cancellationToken);

            if (orphanedConnections.Count > 0)
            {
                _logger.LogWarning("🧹 Cleaning up {Count} orphaned SourceConnections from previous server run", orphanedConnections.Count);

                foreach (var connection in orphanedConnections)
                {
                    connection.DisconnectedAt = now;
                    connection.Status = SourceConnectionStatus.Disconnected;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogWarning("🧹 SourceConnections cleanup complete! Marked {Count} connections as disconnected", orphanedConnections.Count);
            }
            else
            {
                _logger.LogInformation("✅ No orphaned SourceConnections to clean up");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up orphaned sessions/connections on startup");
            // Don't throw - let server start even if cleanup fails
        }
    }

    /// <summary>
    /// Accept incoming TCP connections
    /// </summary>
    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _tcpListener!.AcceptTcpClientAsync(cancellationToken);
                var address = ((IPEndPoint)tcpClient.Client.RemoteEndPoint!).Address.ToString();
                var lease = _admission.TryAcquire(address);
                if (lease is null)
                {
                    tcpClient.Dispose();
                    continue;
                }

                // Disable Nagle's algorithm for low-latency real-time RTCM data
                tcpClient.NoDelay = true;

                var clientId = Guid.NewGuid().ToString();

                // Handle connection in background
                _ = HandleAdmittedConnectionAsync(clientId, tcpClient, lease, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Server is shutting down
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting connection");
            }
        }
    }

    /// <summary>
    /// Handle incoming connection (source or client)
    /// </summary>
    private async Task HandleAdmittedConnectionAsync(string clientId, TcpClient tcpClient, IDisposable lease, CancellationToken cancellationToken)
    {
        using (lease)
            await HandleConnectionAsync(clientId, tcpClient, cancellationToken);
    }

    private async Task HandleConnectionAsync(string clientId, TcpClient tcpClient, CancellationToken cancellationToken)
    {
        try
        {
            using (tcpClient)
            using (var stream = tcpClient.GetStream())
            {
                NtripRequest request;
                try
                {
                    request = await NtripRequestParser.ReadAsync(stream, cancellationToken);
                }
                catch (InvalidDataException ex)
                {
                    _logger.LogWarning(ex, "Invalid NTRIP request from {ClientId}", clientId);
                    await SendResponseAsync(stream, $"HTTP/1.1 400 Bad Request{NtripProtocol.CrLf}{NtripProtocol.CrLf}", cancellationToken);
                    return;
                }

                _logger.LogInformation(
                    "Received request from {ClientId}: {Method} {Path}",
                    clientId,
                    request.Method,
                    request.Path);

                // Determine connection type
                if (request.Method == "SOURCE")
                {
                    await HandleSourceConnectionAsync(clientId, tcpClient, stream, request, useChunkedBody: false, cancellationToken);
                }
                else if (request.Method == "POST")
                {
                    await HandleSourceConnectionAsync(clientId, tcpClient, stream, request, useChunkedBody: true, cancellationToken);
                }
                else if (request.Method == "GET")
                {
                    await HandleClientConnectionAsync(clientId, tcpClient, stream, request, cancellationToken);
                }
                else
                {
                    _logger.LogWarning("Unknown request type from {ClientId}: {Method}", clientId, request.Method);
                    await SendResponseAsync(stream, $"HTTP/1.1 400 Bad Request{NtripProtocol.CrLf}{NtripProtocol.CrLf}", cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling connection {ClientId}", clientId);
        }
    }

    private static string? GetHeader(NtripRequest request, string name)
    {
        return request.Headers.TryGetValue(name, out var value) ? value : null;
    }

    private static string BuildJson(Dictionary<string, object?> values)
    {
        return JsonSerializer.Serialize(values.Where(kvp => kvp.Value != null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
    }

    private async Task RecordDiagnosticEventAsync(
        string kind,
        string severity,
        string message,
        string? userId = null,
        string? clientSessionId = null,
        int? sourceConnectionId = null,
        int? mountPointId = null,
        string? dataJson = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var diagnostics = scope.ServiceProvider.GetRequiredService<IDiagnosticEventService>();

            await diagnostics.RecordAsync(new CreateDiagnosticEventRequest
            {
                Kind = kind,
                Severity = severity,
                UserId = userId,
                ClientSessionId = clientSessionId,
                SourceConnectionId = sourceConnectionId,
                MountPointId = mountPointId,
                Message = message,
                DataJson = dataJson
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record diagnostic event {Kind}", kind);
        }
    }

    /// <summary>
    /// Handle GNSS source connection
    /// SOURCE password mountpoint
    /// (e.g., "SOURCE R8QGsWgrPE test")
    /// </summary>
    private async Task HandleSourceConnectionAsync(
        string sourceId,
        TcpClient tcpClient,
        NetworkStream stream,
        NtripRequest request,
        bool useChunkedBody,
        CancellationToken cancellationToken)
    {
        string? mountPointName = request.MountPoint;
        try
        {
            if (string.IsNullOrEmpty(mountPointName))
            {
                _logger.LogWarning("Source request from {ClientId} did not include a mountpoint", sourceId);
                await SendSourceErrorAsync(stream, request, "400 Bad Request", cancellationToken);
                return;
            }

            var password = request.Method == "SOURCE" ? request.SourcePassword : request.BasicPassword;
            if (string.IsNullOrEmpty(password))
            {
                await SendSourceErrorAsync(stream, request, "401 Unauthorized", cancellationToken);
                return;
            }

            // Authenticate source
            using var scope = _serviceProvider.CreateScope();
            var authService = scope.ServiceProvider.GetRequiredService<NtripAuthenticationService>();
            var mountPoint = await authService.AuthenticateSourceAsync(mountPointName, password, request.BasicUsername);

            if (mountPoint == null)
            {
                await SendSourceErrorAsync(stream, request, "401 Unauthorized", cancellationToken);
                return;
            }

            mountPointName = mountPoint.Name;

            // Register source
            if (!_connectionPool.RegisterSource(sourceId, mountPointName, tcpClient))
            {
                await RecordDiagnosticEventAsync(
                    "DuplicateSourceRejected",
                    "warning",
                    $"Duplicate base station source rejected for mountpoint '{mountPointName}'",
                    mountPointId: mountPoint.Id,
                    cancellationToken: cancellationToken);
                await SendSourceErrorAsync(stream, request, "503 Service Unavailable", cancellationToken);
                return;
            }

            // Send success response
            await SendSourceSuccessAsync(stream, request, cancellationToken);

            // Create SourceConnection in database
            var sourceConnectionId = await CreateSourceConnectionAsync(mountPointName, cancellationToken);
            if (sourceConnectionId.HasValue)
            {
                _sourceConnectionIds[sourceId] = sourceConnectionId.Value;
                _sourceCounters[sourceId] = new SourceDiagnosticCounters();
            }

            // Send Telegram notification for source connected
            await _telegramService.SendSourceConnectedAsync(mountPointName, cancellationToken);

            // Initialize client count for this mount point
            if (!_mountPointClientCounts.ContainsKey(mountPointName))
            {
                _mountPointClientCounts[mountPointName] = 0;
            }

            // Broadcast source data directly to all connected clients using zero-copy SharedRtcmBuffer
            if (useChunkedBody)
            {
                await HandleSourceStreamChunkedAsync(sourceId, stream, mountPointName, cancellationToken);
            }
            else
            {
                await HandleSourceStreamRawAsync(sourceId, stream, mountPointName, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling source connection {SourceId}", sourceId);
        }
        finally
        {
            _connectionPool.UnregisterSource(sourceId);
            // Mark connection as disconnected
            // Note: SourceConnectionId is not stored, so we mark the latest one for this mountpoint
            if (!string.IsNullOrEmpty(mountPointName))
            {
                await MarkSourceConnectionDisconnectedAsync(mountPointName, cancellationToken, sourceId, "Connection closed");
                _sourceConnectionIds.TryRemove(sourceId, out _);
                _sourceCounters.TryRemove(sourceId, out _);

                // Send Telegram notification for source disconnected
                await _telegramService.SendSourceDisconnectedAsync(mountPointName, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Handle client connection
    /// GET /STATION_A HTTP/1.1
    /// Authorization: Basic base64(username:password)
    /// </summary>
    private async Task HandleClientConnectionAsync(
        string clientId,
        TcpClient tcpClient,
        NetworkStream stream,
        NtripRequest request,
        CancellationToken cancellationToken)
    {
        string? mountPointName = request.MountPoint;
        try
        {
            // Empty path = sourcetable request
            if (request.IsSourcetableRequest)
            {
                await HandleSourcetableRequestAsync(stream, cancellationToken);
                return;
            }

            if (string.IsNullOrEmpty(mountPointName))
            {
                await SendResponseAsync(stream, $"HTTP/1.1 400 Bad Request{NtripProtocol.CrLf}{NtripProtocol.CrLf}", cancellationToken);
                return;
            }

            // Extract credentials from Basic auth
            (string? username, string? password) = (request.BasicUsername, request.BasicPassword);
            if (username == null || password == null)
            {
                await SendResponseAsync(stream, $"HTTP/1.1 401 Unauthorized{NtripProtocol.CrLf}{NtripProtocol.CrLf}", cancellationToken);
                return;
            }

            // Authenticate client
            using var scope = _serviceProvider.CreateScope();
            var authService = scope.ServiceProvider.GetRequiredService<NtripAuthenticationService>();
            var authResult = await authService.AuthenticateClientAsync(username, password, mountPointName);

            if (!authResult.Success)
            {
                await SendResponseAsync(stream, $"HTTP/1.1 401 Unauthorized{NtripProtocol.CrLf}{NtripProtocol.CrLf}", cancellationToken);
                return;
            }

            mountPointName = authResult.MountPoint?.Name ?? mountPointName;

            // Register client
            if (!_connectionPool.RegisterClient(clientId, mountPointName, username, tcpClient))
            {
                await SendResponseAsync(stream, $"HTTP/1.1 503 Service Unavailable{NtripProtocol.CrLf}{NtripProtocol.CrLf}", cancellationToken);
                return;
            }

            // Send success response with proper NTRIP headers
            // ICY 200 OK is used for streaming connections (NTRIP protocol)
            var response = string.Join(NtripProtocol.CrLf,
                "ICY 200 OK",
                "Server: AgOpenNtripCaster/1.0",
                "Content-Type: gnss/data",
                "Ntrip-Version: Ntrip/2.0",
                "",
                "");
            await SendResponseAsync(stream, response, cancellationToken);

            // Create ClientSession in database
            var clientIpAddress = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
            var clientUserAgent = GetHeader(request, "User-Agent");
            var sessionId = await CreateClientSessionAsync(username, clientId, mountPointName, clientIpAddress, clientUserAgent, cancellationToken);

            if (!string.IsNullOrEmpty(sessionId))
            {
                _clientSessionIds[clientId] = sessionId;
                _roverCounters[clientId] = new RoverDiagnosticCounters();

                await RecordDiagnosticEventAsync(
                    "RoverConnected",
                    "info",
                    $"Rover '{username}' connected to '{mountPointName}'",
                    userId: authResult.User?.Id,
                    clientSessionId: sessionId,
                    mountPointId: authResult.MountPoint?.Id,
                    dataJson: BuildJson(new Dictionary<string, object?>
                    {
                        ["clientId"] = clientId,
                        ["ipAddress"] = clientIpAddress,
                        ["userAgent"] = clientUserAgent
                    }),
                    cancellationToken: cancellationToken);

                if (_connectionPool.GetSourceForMountPoint(mountPointName) == null)
                {
                    await RecordDiagnosticEventAsync(
                        "NoSourceForRover",
                        "warning",
                        $"Rover '{username}' connected but no active base source exists for '{mountPointName}'",
                        userId: authResult.User?.Id,
                        clientSessionId: sessionId,
                        mountPointId: authResult.MountPoint?.Id,
                        cancellationToken: cancellationToken);
                }
            }

            // Increment client count for this mount point
            if (!_mountPointClientCounts.ContainsKey(mountPointName))
            {
                _mountPointClientCounts[mountPointName] = 0;
            }
            _mountPointClientCounts[mountPointName]++;
            _logger.LogDebug("Client connected: {ClientId} ({Username}) → {MountPoint}, total clients: {Count}",
                clientId, username, mountPointName, _mountPointClientCounts[mountPointName]);

            // Stream RTCM data to client using zero-copy SharedRtcmBuffer from channel
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await HandleClientStreamAsync(clientId, reader, stream, mountPointName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client connection {ClientId}", clientId);
        }
        finally
        {
            // Decrement client count for this mount point
            if (!string.IsNullOrEmpty(mountPointName) && _mountPointClientCounts.ContainsKey(mountPointName))
            {
                var oldCount = _mountPointClientCounts[mountPointName];
                _mountPointClientCounts[mountPointName]--;
                var newCount = _mountPointClientCounts[mountPointName];
                _logger.LogDebug("Client disconnected: {ClientId} → {MountPoint}, count: {Old} → {New}",
                    clientId, mountPointName, oldCount, newCount);
            }

            // Mark session as disconnected
            if (_clientSessionIds.TryGetValue(clientId, out var sessionId))
            {
                await MarkClientSessionDisconnectedAsync(sessionId, cancellationToken, clientId, "Connection closed");
                _clientSessionIds.Remove(clientId);
                _roverCounters.TryRemove(clientId, out _);
            }

            _connectionPool.UnregisterClient(clientId);
        }
    }

    /// <summary>
    /// Handle source streaming - read RTCM from source, create SharedRtcmBuffer, broadcast to all clients (zero-copy)
    /// </summary>
    private async Task HandleSourceStreamRawAsync(
        string sourceId,
        NetworkStream stream,
        string mountPointName,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var rtcmBuffer = new List<byte>();  // Buffer for collecting RTCM message chunks
        _logger.LogInformation("Source {SourceId} starting raw zero-copy stream for {MountPoint}", sourceId, mountPointName);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Read from source
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                if (bytesRead == 0)
                {
                    _logger.LogInformation("Source {SourceId} disconnected (EOF)", sourceId);
                    break;
                }

                await BroadcastRtcmAsync(sourceId, mountPointName, buffer.AsMemory(0, bytesRead), rtcmBuffer, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Source {SourceId} stream cancelled", sourceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in source stream {SourceId}", sourceId);
        }
    }

    private async Task HandleSourceStreamChunkedAsync(
        string sourceId,
        NetworkStream stream,
        string mountPointName,
        CancellationToken cancellationToken)
    {
        var rtcmBuffer = new List<byte>();
        _logger.LogInformation("Source {SourceId} starting chunked zero-copy stream for {MountPoint}", sourceId, mountPointName);

        try
        {
            await NtripChunkedStreamReader.ReadChunksAsync(
                stream,
                (payload, ct) => new ValueTask(BroadcastRtcmAsync(sourceId, mountPointName, payload, rtcmBuffer, ct)),
                cancellationToken);

            _logger.LogInformation("Source {SourceId} chunked stream ended", sourceId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Source {SourceId} chunked stream cancelled", sourceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in chunked source stream {SourceId}", sourceId);
        }
    }

    private async Task BroadcastRtcmAsync(
        string sourceId,
        string mountPointName,
        ReadOnlyMemory<byte> payload,
        List<byte> rtcmBuffer,
        CancellationToken cancellationToken)
    {
        if (payload.Length == 0)
            return;

        var data = payload.ToArray();

        // Parse RTCM messages for station position extraction
        rtcmBuffer.AddRange(data);
        await ParseRtcmMessagesAsync(rtcmBuffer, mountPointName, cancellationToken);

        var clients = _connectionPool.GetClientsForMountPoint(mountPointName);
        if (clients.Count > 0)
        {
            var sharedBuffer = new SharedRtcmBuffer(data);
            var enqueuedCount = 0;

            try
            {
                foreach (var client in clients)
                {
                    sharedBuffer.AddRef();

                    if (client.BufferChannel.Writer.TryWrite(sharedBuffer))
                    {
                        client.PendingBufferCount++;
                        enqueuedCount++;
                    }
                    else
                    {
                        sharedBuffer.Release();

                        _logger.LogWarning("Client {ClientId} channel full, dropping buffer (slow client)", client.Id);
                    }
                }
            }
            finally
            {
                // Keep the producer reference alive until every client has either
                // accepted or rejected its own reference.
                sharedBuffer.Release();
            }

            _logger.LogDebug(
                "Broadcasted {Bytes} bytes from source {SourceId} to {Count} clients via SharedRtcmBuffer",
                payload.Length,
                sourceId,
                enqueuedCount);
        }

        var sourceConnection = _connectionPool.GetSourceForMountPoint(mountPointName);
        if (sourceConnection != null)
        {
            sourceConnection.BytesReceived += payload.Length;
            sourceConnection.LastActivityAt = DateTime.UtcNow;
        }

        if (_sourceCounters.TryGetValue(sourceId, out var sourceCounters))
        {
            sourceCounters.RecordRtcmReceived(payload.Length, DateTime.UtcNow);
            sourceCounters.RecordBytesBroadcastToRovers((long)payload.Length * clients.Count);
        }
    }

    /// <summary>
    /// Parse RTCM messages from buffer, extract complete messages, and update MountPoint position if RTCM1005 detected
    /// Handles message fragmentation across multiple TCP packets
    /// </summary>
    private async Task ParseRtcmMessagesAsync(List<byte> rtcmBuffer, string mountPointName, CancellationToken cancellationToken)
    {
        try
        {
            // Process complete RTCM messages from the buffer
            while (rtcmBuffer.Count >= 6)  // RTCM3 header is 3 bytes minimum
            {
                // Look for RTCM3 preamble (0xD3)
                int preambleIndex = -1;
                for (int i = 0; i < rtcmBuffer.Count; i++)
                {
                    if (rtcmBuffer[i] == 0xD3)
                    {
                        preambleIndex = i;
                        break;
                    }
                }

                // No preamble found, clear buffer (corrupted data)
                if (preambleIndex < 0)
                {
                    _logger.LogDebug("No RTCM3 preamble found in buffer of {Count} bytes for {MountPointName}", rtcmBuffer.Count, mountPointName);
                    rtcmBuffer.Clear();
                    return;
                }

                // Remove data before preamble
                if (preambleIndex > 0)
                {
                    rtcmBuffer.RemoveRange(0, preambleIndex);
                }

                // Extract message length from RTCM3 header (bits 14-23 of first 3 bytes)
                if (rtcmBuffer.Count < 3)
                    return;  // Not enough data yet

                int length = ((rtcmBuffer[1] & 0x03) << 8) | rtcmBuffer[2];
                int messageSize = 3 + length + 3;  // preamble(3) + payload(length) + checksum(3)

                // Not enough data for complete message yet
                if (rtcmBuffer.Count < messageSize)
                    return;

                // Extract complete message
                var messageData = rtcmBuffer.GetRange(0, messageSize).ToArray();
                rtcmBuffer.RemoveRange(0, messageSize);

                // Parse message
                await ParseAndUpdateRtcmDataAsync(messageData, mountPointName, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing RTCM buffer for {MountPointName}", mountPointName);
            rtcmBuffer.Clear();  // Clear on error to prevent stuck state
        }
    }

    /// <summary>
    /// Parse single RTCM message and update MountPoint position if RTCM1005 detected
    /// </summary>
    private async Task ParseAndUpdateRtcmDataAsync(byte[] messageData, string mountPointName, CancellationToken cancellationToken)
    {
        try
        {
            // Parse the RTCM message
            var parseResult = RtcmMessageParser.ParseRtcmMessage(messageData);

            // If RTCM1005 message detected and contains position data
            if (parseResult?.Position1005 != null)
            {
                var position = parseResult.Position1005;
                _logger.LogDebug(
                    "RTCM1005 message detected for {MountPointName}: RefStation={RefStationId}, Lat={Lat}, Lon={Lon}",
                    mountPointName, position.ReferenceStationId, position.Latitude, position.Longitude);

                // Update mount point with RTCM position data
                await UpdateMountPointPositionAsync(
                    mountPointName,
                    position.Latitude,
                    position.Longitude,
                    position.ReferenceStationId,
                    cancellationToken);
            }

            // Log detected format if available
            if (!string.IsNullOrEmpty(parseResult?.DetectedFormat))
            {
                _logger.LogDebug("Detected RTCM format: {Format} for {MountPointName}",
                    parseResult.DetectedFormat, mountPointName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error parsing RTCM message for {MountPointName}", mountPointName);
        }
    }

    /// <summary>
    /// Handle client streaming - read SharedRtcmBuffer from channel, send to client (zero-copy)
    /// Also handles position frames from client
    /// </summary>
    private async Task HandleClientStreamAsync(
        string clientId,
        StreamReader reader,
        NetworkStream stream,
        string mountPointName,
        CancellationToken cancellationToken)
    {
        var lastPositionTime = DateTime.MinValue;  // Position is optional
        var hasReceivedPosition = false;  // Track if we've received any position
        var firstGgaDiagnosticRecorded = false;
        var invalidGgaDiagnosticRecorded = false;
        const int MaxPositionAgeSec = 15;

        _logger.LogInformation("Client {ClientId} starting zero-copy stream for {MountPoint}", clientId, mountPointName);

        try
        {
            // Start reading positions and streaming data concurrently
            var positionTask = ReadPositionFramesAsync(clientId, reader, cancellationToken);
            var streamTask = StreamRtcmDataAsync();

            await Task.WhenAll(positionTask, streamTask);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Client {ClientId} stream cancelled", clientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in client stream {ClientId}", clientId);
        }

        async Task ReadPositionFramesAsync(string cId, StreamReader sr, CancellationToken ct)
        {
            try
            {
                string? line;
                int lineCount = 0;
                while ((line = await sr.ReadLineAsync(ct)) != null)
                {
                    lineCount++;

                    if (GgaFrameParser.TryParse(line, out var ggaFrame))
                    {
                        var now = DateTime.UtcNow;
                        var lineBytes = Encoding.ASCII.GetByteCount(line) + 1;

                        if (_roverCounters.TryGetValue(cId, out var counters))
                        {
                            counters.RecordBytesReceived(lineBytes);
                            counters.RecordGga(ggaFrame!, now);
                        }

                        if (ggaFrame!.IsPositionValid && ggaFrame.Latitude.HasValue && ggaFrame.Longitude.HasValue)
                        {
                            lastPositionTime = now;
                            if (!hasReceivedPosition)
                            {
                                hasReceivedPosition = true;
                                _logger.LogInformation("Client {ClientId} first position received: {Lat},{Lon}", cId, ggaFrame.Latitude, ggaFrame.Longitude);
                            }

                            var clientInfo = _connectionPool.GetClient(cId);
                            if (clientInfo != null)
                            {
                                var accuracy = ggaFrame.Hdop ?? 5.0;
                                clientInfo.LastLatitude = ggaFrame.Latitude.Value;
                                clientInfo.LastLongitude = ggaFrame.Longitude.Value;
                                clientInfo.LastAccuracy = accuracy;
                                clientInfo.LastPositionAt = lastPositionTime;

                                if (_clientSessionIds.TryGetValue(cId, out var sessionId))
                                {
                                    await UpdateClientSessionPositionAsync(sessionId, ggaFrame.Latitude.Value, ggaFrame.Longitude.Value, accuracy, ct);

                                    if (!firstGgaDiagnosticRecorded)
                                    {
                                        firstGgaDiagnosticRecorded = true;
                                        await RecordDiagnosticEventAsync(
                                            "FirstGgaReceived",
                                            "info",
                                            $"First valid GGA received from rover '{clientInfo.Username}'",
                                            userId: null,
                                            clientSessionId: sessionId,
                                            mountPointId: null,
                                            dataJson: BuildJson(new Dictionary<string, object?>
                                            {
                                                ["fixQuality"] = ggaFrame.FixQuality,
                                                ["satellites"] = ggaFrame.SatelliteCount,
                                                ["hdop"] = ggaFrame.Hdop,
                                                ["latitude"] = ggaFrame.Latitude,
                                                ["longitude"] = ggaFrame.Longitude
                                            }),
                                            cancellationToken: ct);
                                    }
                                }

                                var positionUpdate = new ClientPositionUpdate
                                {
                                    ClientId = cId,
                                    Username = clientInfo.Username,
                                    MountPoint = clientInfo.MountPointName,
                                    Latitude = ggaFrame.Latitude.Value,
                                    Longitude = ggaFrame.Longitude.Value,
                                    Accuracy = accuracy,
                                    Timestamp = lastPositionTime
                                };

                                await _hubContext.Clients.All.SendAsync("ClientPositionUpdated", positionUpdate, ct);
                            }
                        }
                        else if (!invalidGgaDiagnosticRecorded && _clientSessionIds.TryGetValue(cId, out var sessionId))
                        {
                            invalidGgaDiagnosticRecorded = true;
                            await RecordDiagnosticEventAsync(
                                "InvalidGga",
                                "warning",
                                "Rover sent GGA without a valid RTK position",
                                clientSessionId: sessionId,
                                dataJson: BuildJson(new Dictionary<string, object?>
                                {
                                    ["fixQuality"] = ggaFrame.FixQuality,
                                    ["satellites"] = ggaFrame.SatelliteCount,
                                    ["hdop"] = ggaFrame.Hdop
                                }),
                                cancellationToken: ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading positions from {ClientId}", cId);
            }
        }

        async Task StreamRtcmDataAsync()
        {
            try
            {
                _logger.LogInformation("Client {ClientId} stream task started (zero-copy)", clientId);

                var client = _connectionPool.GetClient(clientId);
                if (client == null)
                {
                    _logger.LogError("Client {ClientId} not found in connection pool", clientId);
                    return;
                }

                var channelReader = client.BufferChannel.Reader;

                while (!cancellationToken.IsCancellationRequested)
                {
                    // Check position freshness only if we've received a position
                    if (hasReceivedPosition)
                    {
                        var timeSinceLastPos = DateTime.UtcNow - lastPositionTime;
                        if (timeSinceLastPos.TotalSeconds > MaxPositionAgeSec)
                        {
                            // Pause stream if position is too old
                            if (client.IsStreaming)
                            {
                                client.IsStreaming = false;
                                _logger.LogWarning("Client {ClientId} stream paused (stale position)", clientId);

                                if (_roverCounters.TryGetValue(clientId, out var counters) && counters.MarkPaused(DateTime.UtcNow) &&
                                    _clientSessionIds.TryGetValue(clientId, out var sessionId))
                                {
                                    await RecordDiagnosticEventAsync(
                                        "StreamPaused",
                                        "warning",
                                        $"Rover '{client.Username}' stream paused because GGA is stale",
                                        clientSessionId: sessionId,
                                        dataJson: BuildJson(new Dictionary<string, object?>
                                        {
                                            ["positionAgeSeconds"] = timeSinceLastPos.TotalSeconds
                                        }),
                                        cancellationToken: cancellationToken);
                                }
                            }

                            await Task.Delay(1000, cancellationToken);
                            continue;
                        }
                    }

                    // Resume streaming if needed
                    if (!client.IsStreaming)
                    {
                        client.IsStreaming = true;
                        _logger.LogInformation("Client {ClientId} stream active", clientId);

                        if (_roverCounters.TryGetValue(clientId, out var counters) && counters.MarkResumed(DateTime.UtcNow) &&
                            _clientSessionIds.TryGetValue(clientId, out var sessionId))
                        {
                            await RecordDiagnosticEventAsync(
                                "StreamResumed",
                                "info",
                                $"Rover '{client.Username}' stream resumed after fresh GGA",
                                clientSessionId: sessionId,
                                cancellationToken: cancellationToken);
                        }
                    }

                    // Read SharedRtcmBuffer from channel (waits if no data available)
                    SharedRtcmBuffer? sharedBuffer = null;
                    try
                    {
                        if (await channelReader.WaitToReadAsync(cancellationToken))
                        {
                            if (channelReader.TryRead(out sharedBuffer))
                            {
                                client.PendingBufferCount--;

                                // Check if client is still connected before writing
                                if (client.TcpClient?.Connected == false)
                                {
                                    _logger.LogInformation("Client {ClientId} disconnected (TcpClient.Connected=false)", clientId);
                                    sharedBuffer.Release(); // Release our reference
                                    break;
                                }

                                try
                                {
                                    // ZERO-COPY: Write ReadOnlyMemory directly to network stream
                                    await stream.WriteAsync(sharedBuffer.Data, cancellationToken);
                                    await stream.FlushAsync(cancellationToken);

                                    // Update statistics
                                    client.BytesSent += sharedBuffer.Length;
                                    client.LastActivityAt = DateTime.UtcNow;
                                    if (_roverCounters.TryGetValue(clientId, out var counters))
                                    {
                                        counters.RecordBytesSent(sharedBuffer.Length);
                                        counters.RecordPendingBufferCount(Math.Max(client.PendingBufferCount, 0));
                                    }

                                    _logger.LogDebug("Client {ClientId} sent {Bytes} bytes (zero-copy)", clientId, sharedBuffer.Length);
                                }
                                catch (IOException ioEx) when (ioEx.InnerException is SocketException socketEx)
                                {
                                    _logger.LogInformation("Client {ClientId} disconnected during stream: {Message}",
                                        clientId, socketEx.Message);
                                    if (_clientSessionIds.TryGetValue(clientId, out var sessionId))
                                    {
                                        await RecordDiagnosticEventAsync(
                                            "WriteFailed",
                                            "warning",
                                            $"Rover '{client.Username}' disconnected during RTCM write",
                                            clientSessionId: sessionId,
                                            dataJson: BuildJson(new Dictionary<string, object?>
                                            {
                                                ["socketError"] = socketEx.Message
                                            }),
                                            cancellationToken: cancellationToken);
                                    }
                                    break;
                                }
                                finally
                                {
                                    // Release buffer reference (decrements refCount, disposes if 0)
                                    sharedBuffer.Release();
                                }
                            }
                        }
                        else
                        {
                            // Channel completed/closed
                            _logger.LogInformation("Client {ClientId} channel closed", clientId);
                            break;
                        }
                    }
                    catch (Exception chanEx)
                    {
                        _logger.LogWarning(chanEx, "Client {ClientId} channel read error", clientId);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error streaming to {ClientId}", clientId);
            }
        }
    }

    /// <summary>
    /// Convert NMEA DDMM.MMMM format to decimal degrees
    /// </summary>
    private double? ConvertNmeaToDecimal(double nmeaValue, bool isNegative)
    {
        if (nmeaValue < 0)
        {
            _logger.LogWarning("⚠️ ConvertNmeaToDecimal: negative value {Value}", nmeaValue);
            return null;
        }

        // Extract degrees (integer part / 100)
        int degrees = (int)(nmeaValue / 100);

        // Extract minutes (remainder / 100)
        double minutes = nmeaValue - (degrees * 100);

        // Convert to decimal degrees
        double decimalDegrees = degrees + (minutes / 60.0);

        // Apply sign if negative (South or West)
        if (isNegative)
            decimalDegrees = -decimalDegrees;

        _logger.LogInformation("🔄 ConvertNmeaToDecimal: input={Input}, degrees={Deg}, minutes={Min}, result={Result}, isNegative={IsNeg}",
            nmeaValue, degrees, minutes, decimalDegrees, isNegative);

        return decimalDegrees;
    }

    /// <summary>
    /// Handle sourcetable request: GET /
    /// Returns list of CONNECTED mount points in NTRIP 2.0 format
    /// Only includes sources that have active GNSS station connections
    /// </summary>
    private async Task HandleSourcetableRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            // Create a scoped DbContext for this request
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Get ALL active mount points from database
            var allMountPoints = await dbContext.MountPoints
                .Where(m => m.IsActive)
                .ToListAsync();

            var lines = new List<string>();

            // NTRIP 2.0 sourcetable format
            lines.Add("SOURCETABLE 200 OK");

            // Get CAS and NET info from database
            var casterInfo = await dbContext.CasterInfos.FirstOrDefaultAsync(cancellationToken);
            var networkInfo = await dbContext.NetworkInfos.FirstOrDefaultAsync(cancellationToken);

            // CAS entry (Caster Info)
            // CAS;identifier;operator;nmea;country;lat;lon;fallback_host;port;misc
            if (casterInfo != null)
            {
                lines.Add(
                    $"CAS;{casterInfo.Identifier};{casterInfo.Operator};{casterInfo.NmeaSupport};{casterInfo.Country};{casterInfo.Latitude:F1};{casterInfo.Longitude:F1};{casterInfo.FallbackHost ?? ""};{casterInfo.Port};{casterInfo.Description}");
            }
            else
            {
                // Fallback if no config found
                _logger.LogWarning("No CasterInfo configured, using defaults for sourcetable");
                lines.Add("CAS;agopencast;AgOpenNtripCaster;0;FI;60.0;24.0;;2101;AgOpen GNSS RTK Server");
            }

            // NET entry (Network Info) - optional but recommended
            // NET;identifier;operator;auth;fee;website;email;startdate;enddate
            if (networkInfo != null)
            {
                lines.Add(
                    $"NET;{networkInfo.Identifier};{networkInfo.Operator};{networkInfo.AuthenticationRequired};{networkInfo.FeeRequired};{networkInfo.Website};{networkInfo.Email};{networkInfo.StartDate:yyyy-MM-dd};{networkInfo.EndDate:yyyy-MM-dd}");
            }
            else
            {
                // Fallback if no config found
                _logger.LogWarning("No NetworkInfo configured, using defaults for sourcetable");
                lines.Add("NET;NTRIP;AgOpenNtripCaster;Y;N;https://github.com/AgOpenGPS;info@agopenrtk.local;2025-01-01;2026-12-31");
            }

            // STR entries - include configured active mount points so AgOpenGPS can select
            // the stream before the base station source has connected.
            foreach (var mp in allMountPoints)
            {
                // Use RTCM-extracted coordinates if available, otherwise fallback to static coordinates
                var latitude = mp.RtcmLatitude ?? mp.Latitude ?? 0m;
                var longitude = mp.RtcmLongitude ?? mp.Longitude ?? 0m;

                // NTRIP 2.0 Sourcetable format:
                // STR;mountpoint;identifier;format;format-details;carrier;nav-system;network;country;lat;lon;nmea;solution;generator;compression;auth;fee;bitrate;misc
                var identifier = mp.Identifier ?? mp.Description ?? "Unknown";
                var format = mp.DetectedFormat?.Replace("RTCM3", "RTCM 3") ?? "RTCM 3";  // NTRIP spec uses space
                var formatDetails = mp.FormatDetails ?? "1005,1077,1087,1097";  // Default RTCM message types
                var carrier = mp.Carrier.ToString();  // 0=No, 1=L1, 2=L1+L2
                var navSystems = mp.NavSystem ?? mp.DetectedNavSystems ?? "GPS";  // Use configured or auto-detected
                var network = mp.Network;
                var country = mp.Country;
                var nmea = mp.NmeaRequired ? "1" : "0";
                var solution = mp.Solution.ToString();  // 0=single base, 1=network
                var generator = mp.Generator;
                var compression = mp.Compression;
                var auth = mp.Authentication;  // N, B, D, or B,D
                var fee = mp.FeeRequired ? "Y" : "N";
                var bitrate = mp.BytesPerSecond ?? 5000;
                var misc = mp.Misc ?? "";

                lines.Add(
                    $"STR;{mp.Name};{identifier};{format};{formatDetails};{carrier};{navSystems};{network};{country};{latitude:F2};{longitude:F2};{nmea};{solution};{generator};{compression};{auth};{fee};{bitrate};{misc}");
            }

            lines.Add("ENDSOURCETABLE");
            lines.Add(string.Empty);

            var response = Encoding.ASCII.GetBytes(string.Join(NtripProtocol.CrLf, lines));
            await stream.WriteAsync(response, 0, response.Length, cancellationToken);

            _logger.LogInformation("Sourcetable sent with {Total} active mount points", allMountPoints.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling sourcetable request");
        }
    }

    private static Task SendSourceSuccessAsync(
        NetworkStream stream,
        NtripRequest request,
        CancellationToken cancellationToken)
    {
        var response = request.Method == "POST"
            ? string.Join(NtripProtocol.CrLf,
                "HTTP/1.1 200 OK",
                "Ntrip-Version: Ntrip/2.0",
                "Server: AgOpenNtripCaster/1.0",
                "Connection: close",
                "",
                "")
            : string.Join(NtripProtocol.CrLf, "ICY 200 OK", "", "");

        return SendResponseAsync(stream, response, cancellationToken);
    }

    private static Task SendSourceErrorAsync(
        NetworkStream stream,
        NtripRequest request,
        string status,
        CancellationToken cancellationToken)
    {
        var response = request.Method == "POST"
            ? $"HTTP/1.1 {status}{NtripProtocol.CrLf}{NtripProtocol.CrLf}"
            : $"ERROR - {status}{NtripProtocol.CrLf}";

        return SendResponseAsync(stream, response, cancellationToken);
    }

    /// <summary>
    /// Send HTTP response
    /// </summary>
    private static async Task SendResponseAsync(NetworkStream stream, string response, CancellationToken cancellationToken)
    {
        var data = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(data, 0, data.Length, cancellationToken);
    }

    /// <summary>
    /// Extract username and password from Basic auth header
    /// Format: "Authorization: Basic base64(username:password)"
    /// </summary>
    private static (string?, string?) ExtractBasicAuth(string? authHeader)
    {
        if (string.IsNullOrEmpty(authHeader))
            return (null, null);

        try
        {
            const string prefix = "Basic ";
            var prefixIndex = authHeader.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (prefixIndex < 0)
                return (null, null);

            var base64 = authHeader.Substring(prefixIndex + prefix.Length).Trim();
            var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(base64));
            var parts = decoded.Split(':');

            if (parts.Length != 2)
                return (null, null);

            return (parts[0], parts[1]);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Create a ClientSession in the database with sequential serial number
    /// Serial number is based on count of connected clients for the user
    /// </summary>
    private async Task<string?> CreateClientSessionAsync(
        string username,
        string clientId,
        string mountPointName,
        string? clientIpAddress,
        string? clientUserAgent,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var activityService = scope.ServiceProvider.GetRequiredService<IActivityService>();

            // Get mount point
            var mountPoint = await dbContext.MountPoints
                .FirstOrDefaultAsync(m => m.Name == mountPointName, cancellationToken);
            if (mountPoint == null)
            {
                _logger.LogWarning("Mount point not found: {MountPointName}", mountPointName);
                return null;
            }

            // Get user
            var user = await dbContext.Users
                .FirstOrDefaultAsync(u => u.UserName == username, cancellationToken);
            if (user == null)
            {
                _logger.LogWarning("User not found: {Username}", username);
                return null;
            }

            // Use execution strategy with transaction for retry compatibility
            // This ensures atomic read-modify-write for serial number calculation
            var strategy = dbContext.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                using var transaction = await dbContext.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable, cancellationToken);

                try
                {
                    // Calculate serial number: Count ACTIVE sessions + 1
                    // With serializable isolation, this prevents race conditions on concurrent connections
                    // Serial numbers reset when all sessions disconnect (1st active=1, 2nd active=2, etc.)
                    var activeSessionCount = await dbContext.ClientSessions
                        .Where(cs => cs.UserId == user.Id && cs.DisconnectedAt == null)
                        .CountAsync(cancellationToken);
                    var serialNumber = activeSessionCount + 1;

                    _logger.LogDebug("Serial number assigned: User={Username}, ActiveCount={Count}, Serial={Serial}",
                        username, activeSessionCount, serialNumber);

                    // Create session
                    var session = new ClientSession
                    {
                        Id = Guid.NewGuid().ToString(),
                        UserId = user.Id,
                        MountPointId = mountPoint.Id,
                        ClientIpAddress = clientIpAddress,
                        ClientUserAgent = clientUserAgent,
                        SerialNumber = serialNumber,
                        ConnectedAt = DateTime.UtcNow,
                        Status = ClientStreamStatus.Connected
                    };

                    dbContext.ClientSessions.Add(session);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    _logger.LogInformation(
                        "ClientSession created: {SessionId} for {Username} (serial #{SerialNumber})",
                        session.Id, username, serialNumber);

                    // Log activity
                    await activityService.LogActivityAsync(
                        ActivityType.ClientConnected,
                        mountPoint.Id,
                        user.Id,
                        $"Rover '{username}' connected (#{serialNumber})");

                    // Broadcast updated dashboard stats
                    await BroadcastDashboardStatsAsync(cancellationToken);

                    return session.Id;
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    _logger.LogError(ex, "Error creating client session for {Username}", username);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating ClientSession for {Username}", username);
            return null;
        }
    }

    /// <summary>
    /// Update ClientSession position in database
    /// </summary>
    private async Task UpdateClientSessionPositionAsync(
        string sessionId,
        double latitude,
        double longitude,
        double accuracy,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var session = await dbContext.ClientSessions
                .FirstOrDefaultAsync(cs => cs.Id == sessionId, cancellationToken);
            if (session != null)
            {
                session.LastLatitude = latitude;
                session.LastLongitude = longitude;
                session.LastAccuracy = accuracy;
                session.LastPositionAt = DateTime.UtcNow;

                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating ClientSession position: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Mark ClientSession as disconnected
    /// </summary>
    private async Task MarkClientSessionDisconnectedAsync(
        string sessionId,
        CancellationToken cancellationToken,
        string? clientId = null,
        string? disconnectReason = null)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var activityService = scope.ServiceProvider.GetRequiredService<IActivityService>();

            var session = await dbContext.ClientSessions
                .Include(cs => cs.User)
                .FirstOrDefaultAsync(cs => cs.Id == sessionId, cancellationToken);
            if (session != null)
            {
                _logger.LogError("🔴 MARKING DISCONNECTED: sessionId={SessionId}, username={Username}, clientId={ClientId}",
                    sessionId, session.User?.UserName, clientId);

                session.DisconnectedAt = DateTime.UtcNow;
                session.Status = ClientStreamStatus.Disconnected;
                session.DisconnectReason = disconnectReason;

                if (!string.IsNullOrEmpty(clientId) && _roverCounters.TryGetValue(clientId, out var counters))
                {
                    session.BytesSent = counters.BytesSent;
                    session.BytesReceived = counters.BytesReceived;
                    session.FirstGgaAt = counters.FirstGgaAt;
                    session.GgaFrameCount = counters.GgaFrameCount;
                    session.InvalidGgaFrameCount = counters.InvalidGgaFrameCount;
                    session.LastLatitude = counters.LastValidLatitude ?? session.LastLatitude;
                    session.LastLongitude = counters.LastValidLongitude ?? session.LastLongitude;
                    session.LastAccuracy = counters.LastAccuracy ?? session.LastAccuracy;
                    session.LastFixQuality = counters.LastFixQuality;
                    session.LastSatelliteCount = counters.LastSatelliteCount;
                    session.LastHdop = counters.LastHdop;
                    session.LastAltitudeMeters = counters.LastAltitudeMeters;
                    session.LastGeoidSeparationMeters = counters.LastGeoidSeparationMeters;
                    session.LastDifferentialAgeSeconds = counters.LastDifferentialAgeSeconds;
                    session.LastDifferentialStationId = counters.LastDifferentialStationId;
                    session.StalePositionPeriods = counters.StalePositionPeriods;
                    session.StreamPausedSeconds = counters.StreamPausedSeconds;
                    session.PeakPendingBufferCount = counters.PeakPendingBufferCount;
                }

                await dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogError("🔴 SAVED TO DB: sessionId={SessionId}, DisconnectedAt={DisconnectedAt}",
                    sessionId, session.DisconnectedAt);

                // Log activity
                var userName = session.User?.UserName ?? "Unknown";
                await activityService.LogActivityAsync(
                    ActivityType.ClientDisconnected,
                    session.MountPointId,
                    session.UserId,
                    $"Rover '{userName}' disconnected (#{session.SerialNumber})");

                await RecordDiagnosticEventAsync(
                    "RoverDisconnected",
                    "info",
                    $"Rover '{userName}' disconnected",
                    userId: session.UserId,
                    clientSessionId: session.Id,
                    mountPointId: session.MountPointId,
                    dataJson: BuildJson(new Dictionary<string, object?>
                    {
                        ["reason"] = disconnectReason,
                        ["bytesSent"] = session.BytesSent,
                        ["bytesReceived"] = session.BytesReceived,
                        ["ggaFrames"] = session.GgaFrameCount,
                        ["invalidGgaFrames"] = session.InvalidGgaFrameCount,
                        ["stalePositionPeriods"] = session.StalePositionPeriods
                    }),
                    cancellationToken: cancellationToken);

                // Notify SignalR about client disconnection (for real-time dashboard updates)
                if (!string.IsNullOrEmpty(clientId))
                {
                    await _hubContext.Clients.All.SendAsync(
                        "ClientDisconnected",
                        new { clientId = clientId, username = userName });
                }

                // Broadcast updated dashboard stats
                await BroadcastDashboardStatsAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking ClientSession disconnected: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Create SourceConnection in database
    /// </summary>
    private async Task<int?> CreateSourceConnectionAsync(
        string mountPointName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var activityService = scope.ServiceProvider.GetRequiredService<IActivityService>();

            // Get mount point with Owner navigation property
            var mountPoint = await dbContext.MountPoints
                .Include(m => m.Owner)
                .FirstOrDefaultAsync(m => m.Name == mountPointName, cancellationToken);
            if (mountPoint == null)
            {
                _logger.LogWarning("Mount point not found: {MountPointName}", mountPointName);
                return null;
            }

            // Create connection
            var connection = new SourceConnection
            {
                MountPointId = mountPoint.Id,
                ConnectedAt = DateTime.UtcNow,
                Status = SourceConnectionStatus.Streaming
            };

            dbContext.SourceConnections.Add(connection);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "SourceConnection created for mount point {MountPointName}",
                mountPointName);

            // Log activity
            await activityService.LogActivityAsync(
                ActivityType.SourceConnected,
                mountPoint.Id,
                null,
                $"Base station '{mountPointName}' connected");

            await RecordDiagnosticEventAsync(
                "SourceConnected",
                "info",
                $"Base station '{mountPointName}' connected",
                sourceConnectionId: connection.Id,
                mountPointId: mountPoint.Id,
                cancellationToken: cancellationToken);

            // Broadcast updated dashboard stats
            await BroadcastDashboardStatsAsync(cancellationToken);

            // Broadcast mount point status change
            await BroadcastMountPointStatusAsync(mountPoint, cancellationToken);

            // Broadcast SourceConnected event (new - for granular tracking)
            await _hubContext.Clients.All.SendAsync("SourceConnected", new
            {
                mountPointId = mountPoint.Id,
                mountPointName = mountPoint.Name,
                sourceId = connection.Id.ToString(),
                connectedAt = DateTime.UtcNow.ToString("o"),
                latitude = mountPoint.RtcmLatitude ?? mountPoint.Latitude,
                longitude = mountPoint.RtcmLongitude ?? mountPoint.Longitude
            }, cancellationToken);

            // Send email notifications when source comes online
            try
            {
                var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
                var emailTriggerSettingsService = scope.ServiceProvider.GetRequiredService<IEmailTriggerSettingsService>();
                var emailSettings = await emailTriggerSettingsService.GetSettingsAsync();

                if (emailSettings.SendSourceOnlineEmail)
                {
                    // Get owner email (if user-owned) and admin email
                    var ownerEmail = mountPoint.Owner?.Email;
                    var adminEmail = emailSettings.AdminEmailForSourceNotifications;

                    // Send to owner if it's a user-owned source
                    if (!string.IsNullOrEmpty(ownerEmail))
                    {
                        await emailService.SendSourceOnlineEmailAsync(
                            ownerEmail,
                            mountPoint.Owner!.FullName ?? mountPoint.Owner.UserName ?? "User",
                            mountPoint.Name,
                            mountPointName);
                        _logger.LogInformation("Source online email sent to owner: {Email}", ownerEmail);
                    }

                    // Always send to admin
                    if (!string.IsNullOrEmpty(adminEmail) && adminEmail != ownerEmail)
                    {
                        await emailService.SendSourceOnlineEmailAsync(
                            adminEmail,
                            "Administrator",
                            mountPoint.Name,
                            mountPointName);
                        _logger.LogInformation("Source online email sent to admin: {Email}", adminEmail);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending source online email for {MountPointName}", mountPointName);
                // Don't throw - let the connection succeed even if email fails
            }

            return connection.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating SourceConnection for {MountPointName}", mountPointName);
            return null;
        }
    }

    /// <summary>
    /// Mark SourceConnection as disconnected
    /// </summary>
    private async Task MarkSourceConnectionDisconnectedAsync(
        string mountPointName,
        CancellationToken cancellationToken,
        string? sourceId = null,
        string? disconnectReason = null)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var activityService = scope.ServiceProvider.GetRequiredService<IActivityService>();

            // Find the latest active connection for this mount point
            var connection = await dbContext.SourceConnections
                .Include(sc => sc.MountPoint)
                .ThenInclude(m => m!.Owner)
                .Where(sc => sc.MountPoint!.Name == mountPointName && sc.DisconnectedAt == null)
                .OrderByDescending(sc => sc.ConnectedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (connection != null)
            {
                connection.DisconnectedAt = DateTime.UtcNow;
                connection.Status = SourceConnectionStatus.Disconnected;
                connection.DisconnectReason = disconnectReason;

                if (!string.IsNullOrEmpty(sourceId) && _sourceCounters.TryGetValue(sourceId, out var counters))
                {
                    connection.BytesReceived = counters.BytesReceived;
                    connection.BytesSent = counters.BytesSent;
                    connection.LastRtcmAt = counters.LastRtcmAt;
                    connection.RtcmChunkCount = counters.RtcmChunkCount;
                }

                await dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "SourceConnection marked disconnected for mount point {MountPointName}",
                    mountPointName);

                // Log activity
                await activityService.LogActivityAsync(
                    ActivityType.SourceDisconnected,
                    connection.MountPointId,
                    null,
                    $"Base station '{mountPointName}' disconnected");

                await RecordDiagnosticEventAsync(
                    "SourceDisconnected",
                    "info",
                    $"Base station '{mountPointName}' disconnected",
                    sourceConnectionId: connection.Id,
                    mountPointId: connection.MountPointId,
                    dataJson: BuildJson(new Dictionary<string, object?>
                    {
                        ["reason"] = disconnectReason,
                        ["bytesReceived"] = connection.BytesReceived,
                        ["bytesSent"] = connection.BytesSent,
                        ["rtcmChunks"] = connection.RtcmChunkCount
                    }),
                    cancellationToken: cancellationToken);

                // Broadcast updated dashboard stats
                await BroadcastDashboardStatsAsync(cancellationToken);

                // Broadcast mount point status change
                if (connection.MountPoint != null)
                {
                    await BroadcastMountPointStatusAsync(connection.MountPoint, cancellationToken);
                }

                // Broadcast SourceDisconnected event (new - for granular tracking)
                await _hubContext.Clients.All.SendAsync("SourceDisconnected", new
                {
                    mountPointId = connection.MountPointId,
                    mountPointName = mountPointName,
                    sourceId = connection.Id.ToString(),
                    disconnectedAt = DateTime.UtcNow.ToString("o")
                }, cancellationToken);

                // Send email notifications when source goes offline
                try
                {
                    var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
                    var emailTriggerSettingsService = scope.ServiceProvider.GetRequiredService<IEmailTriggerSettingsService>();
                    var emailSettings = await emailTriggerSettingsService.GetSettingsAsync();

                    if (emailSettings.SendSourceOfflineEmail && connection.MountPoint != null)
                    {
                        // Get owner email (if user-owned) and admin email
                        var ownerEmail = connection.MountPoint.Owner?.Email;
                        var adminEmail = emailSettings.AdminEmailForSourceNotifications;

                        // Send to owner if it's a user-owned source
                        if (!string.IsNullOrEmpty(ownerEmail))
                        {
                            await emailService.SendSourceOfflineEmailAsync(
                                ownerEmail,
                                connection.MountPoint.Owner!.FullName ?? connection.MountPoint.Owner.UserName ?? "User",
                                connection.MountPoint.Name,
                                mountPointName);
                            _logger.LogInformation("Source offline email sent to owner: {Email}", ownerEmail);
                        }

                        // Always send to admin
                        if (!string.IsNullOrEmpty(adminEmail) && adminEmail != ownerEmail)
                        {
                            await emailService.SendSourceOfflineEmailAsync(
                                adminEmail,
                                "Administrator",
                                connection.MountPoint.Name,
                                mountPointName);
                            _logger.LogInformation("Source offline email sent to admin: {Email}", adminEmail);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending source offline email for {MountPointName}", mountPointName);
                    // Don't throw - let the disconnection proceed even if email fails
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking SourceConnection disconnected for {MountPointName}", mountPointName);
        }
    }

    /// <summary>
    /// Perform health check on all active client and source connections
    /// Detects and removes stale connections (hard disconnects, network failures)
    /// Called every 10 seconds by timer
    /// </summary>
    private async Task PerformClientHealthCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            bool anyStaleFound = false;

            // Check all active client connections
            foreach (var client in _connectionPool.GetAllActiveClients())
            {
                bool isStale = false;
                string staleReason = "";

                try
                {
                    // Check if socket is still connected using multiple methods
                    if (client.TcpClient?.Connected == false)
                    {
                        isStale = true;
                        staleReason = "TCP disconnected";
                    }
                    else if (client.TcpClient?.Client?.Poll(0, SelectMode.SelectError) == true)
                    {
                        isStale = true;
                        staleReason = "Socket error";
                    }
                    // Check for inactivity timeout (30 seconds without sending data)
                    else
                    {
                        var inactiveSeconds = (DateTime.UtcNow - client.LastActivityAt).TotalSeconds;
                        if (inactiveSeconds > StaleConnectionTimeoutSeconds)
                        {
                            isStale = true;
                            staleReason = $"Inactive for {inactiveSeconds:F0}s (timeout: {StaleConnectionTimeoutSeconds}s)";
                        }
                        // ZERO-COPY: Check if client has too many pending buffers (backlog)
                        // This prevents slow clients from exhausting memory
                        else if (client.IsStreaming && client.PendingBufferCount > 0)
                        {
                            const int MaxPendingBuffers = 31; // Channel capacity is 32, warn if nearly full

                            if (client.PendingBufferCount >= MaxPendingBuffers)
                            {
                                isStale = true;
                                staleReason = $"Too many pending buffers ({client.PendingBufferCount}, max {MaxPendingBuffers}) - client not receiving data fast enough";
                                _logger.LogWarning("⚠️ SLOW CLIENT: {ClientId} has {Pending} pending buffers, disconnecting",
                                    client.Id, client.PendingBufferCount);
                            }
                        }
                    }
                    // Note: We do NOT check SelectRead with Available == 0
                    // because a connected socket can be readable with 0 bytes available
                    // when waiting for data. This is not a disconnection indicator.
                }
                catch (ObjectDisposedException)
                {
                    isStale = true;
                    staleReason = "Object disposed";
                }
                catch (Exception ex)
                {
                    isStale = true;
                    staleReason = $"Exception: {ex.Message}";
                }

                if (isStale)
                {
                    // Unregister from connection pool
                    _connectionPool.UnregisterClient(client.Id);
                    _logger.LogWarning("🧹 STALE CLIENT CLEANUP: {ClientId} ({Username}@{MountPoint}) - Reason: {Reason}",
                        client.Id, client.Username, client.MountPointName, staleReason);

                    // Mark session as disconnected in database
                    if (_clientSessionIds.TryGetValue(client.Id, out var sessionId))
                    {
                        await MarkClientSessionDisconnectedAsync(sessionId, cancellationToken, client.Id, staleReason);
                        _clientSessionIds.Remove(client.Id);
                    }

                    anyStaleFound = true;
                }
            }

            // Check all active source connections
            foreach (var source in _connectionPool.GetAllActiveSources())
            {
                bool isStale = false;
                string staleReason = "";

                try
                {
                    // Check if socket is still connected using multiple methods
                    if (source.TcpClient?.Connected == false)
                    {
                        isStale = true;
                        staleReason = "TCP disconnected";
                    }
                    else if (source.TcpClient?.Client?.Poll(0, SelectMode.SelectError) == true)
                    {
                        isStale = true;
                        staleReason = "Socket error";
                    }
                    // Check for inactivity timeout (30 seconds without sending data)
                    else
                    {
                        var inactiveSeconds = (DateTime.UtcNow - source.LastActivityAt).TotalSeconds;
                        if (inactiveSeconds > StaleConnectionTimeoutSeconds)
                        {
                            isStale = true;
                            staleReason = $"Inactive for {inactiveSeconds:F0}s (timeout: {StaleConnectionTimeoutSeconds}s)";
                        }
                    }
                    // Note: We do NOT check SelectRead with Available == 0
                    // because a connected socket can be readable with 0 bytes available
                    // when waiting for data. This is not a disconnection indicator.
                }
                catch (ObjectDisposedException)
                {
                    isStale = true;
                    staleReason = "Object disposed";
                }
                catch (Exception ex)
                {
                    isStale = true;
                    staleReason = $"Exception: {ex.Message}";
                }

                if (isStale)
                {
                    // Unregister from connection pool
                    _connectionPool.UnregisterSource(source.Id);
                    _logger.LogWarning("🧹 STALE SOURCE CLEANUP: {SourceId} ({MountPoint}) - Reason: {Reason}",
                        source.Id, source.MountPointName, staleReason);

                    // Mark connection as disconnected in database
                    await MarkSourceConnectionDisconnectedAsync(source.MountPointName, cancellationToken, source.Id, staleReason);

                    anyStaleFound = true;
                }
            }

            // Cleanup old disconnected entries from ConnectionPool (prevent dictionary bloat)
            // Remove entries that have been disconnected for more than 5 minutes
            var cleanupThreshold = DateTime.UtcNow.AddMinutes(-5);
            var oldDisconnectedClients = _connectionPool.GetAllActiveClients()
                .Where(c => c.IsDisconnected && c.DisconnectedAt < cleanupThreshold)
                .ToList();

            foreach (var client in oldDisconnectedClients)
            {
                _connectionPool.UnregisterClient(client.Id);
                _logger.LogInformation("🗑️ Removed old disconnected client from pool: {ClientId}", client.Id);
            }

            var oldDisconnectedSources = _connectionPool.GetAllActiveSources()
                .Where(s => s.IsDisconnected && s.DisconnectedAt < cleanupThreshold)
                .ToList();

            foreach (var source in oldDisconnectedSources)
            {
                _connectionPool.UnregisterSource(source.Id);
                _logger.LogInformation("🗑️ Removed old disconnected source from pool: {SourceId}", source.Id);
            }

            // If any stale connections found, broadcast updated stats
            if (anyStaleFound || oldDisconnectedClients.Any() || oldDisconnectedSources.Any())
            {
                _logger.LogInformation("Stale connections detected and cleaned up. Broadcasting updated stats.");
                await BroadcastDashboardStatsAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing client health check");
        }
    }

    /// <summary>
    /// Calculate current dashboard stats and broadcast via SignalR
    /// Called whenever clients or sources connect/disconnect
    /// </summary>
    private async Task BroadcastDashboardStatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Get active client sessions (not disconnected) - read-only, no tracking
            var activeClients = await dbContext.ClientSessions
                .AsNoTracking()
                .Where(cs => cs.DisconnectedAt == null)
                .ToListAsync(cancellationToken);

            // Get active source connections (not disconnected) - read-only, no tracking
            var activeSources = await dbContext.SourceConnections
                .AsNoTracking()
                .Where(sc => sc.DisconnectedAt == null)
                .ToListAsync(cancellationToken);

            // Count unique mount points with active sources
            var uniqueActiveMountPoints = activeSources.DistinctBy(sc => sc.MountPointId).Count();

            // Calculate total bytes
            var totalBytesReceived = activeClients.Sum(cs => cs.BytesReceived);
            var totalBytesSent = activeClients.Sum(cs => cs.BytesSent);

            // Convert to MB
            const long bytesPerMB = 1048576;
            var receivedMB = totalBytesReceived / (double)bytesPerMB;
            var sentMB = totalBytesSent / (double)bytesPerMB;

            // Calculate uptime
            var now = DateTime.UtcNow;
            var uptime = now - _serverStartTime;
            var uptimeFormatted = FormatUptime(uptime);

            // Create stats object
            var stats = new
            {
                activeClients = activeClients.Count,
                activeSources = uniqueActiveMountPoints,
                totalBytesReceived = receivedMB,
                totalBytesSent = sentMB,
                totalBytesTransferred = receivedMB + sentMB,
                serverStartTime = _serverStartTime.ToString("o"),
                currentTime = now.ToString("o"),
                uptimeFormatted = uptimeFormatted,
                rtcmListenerActive = _tcpListener != null && !_cancellationTokenSource!.Token.IsCancellationRequested
            };

            // Broadcast to all connected SignalR clients
            await _hubContext.Clients.All.SendAsync("DashboardStatsUpdated", stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting dashboard stats");
        }
    }

    /// <summary>
    /// Broadcast mount point status update including current active source count
    /// </summary>
    private async Task BroadcastMountPointStatusAsync(MountPoint mountPoint, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Count active sources for this mount point
            var activeSourceCount = await dbContext.SourceConnections
                .CountAsync(sc => sc.MountPointId == mountPoint.Id && sc.DisconnectedAt == null, cancellationToken);

            // Count active clients for this mount point
            var activeClientCount = await dbContext.ClientSessions
                .CountAsync(cs => cs.MountPointId == mountPoint.Id && cs.DisconnectedAt == null, cancellationToken);

            var statusUpdate = new
            {
                mountPointId = mountPoint.Id,
                mountPointName = mountPoint.Name,
                activeSourceCount = activeSourceCount,
                activeClientCount = activeClientCount,
                updatedAt = DateTime.UtcNow.ToString("o")
            };

            await _hubContext.Clients.All.SendAsync("MountPointStatusChanged", statusUpdate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting mount point status for {MountPointName}", mountPoint.Name);
        }
    }

    /// <summary>
    /// Format uptime duration into human-readable string
    /// </summary>
    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays > 1)
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours > 1)
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
        if (uptime.TotalMinutes > 1)
            return $"{(int)uptime.TotalMinutes}m";
        return $"{uptime.Seconds}s";
    }

    /// <summary>
    /// Update MountPoint with position data extracted from RTCM1005 messages
    /// </summary>
    private async Task UpdateMountPointPositionAsync(
        string mountPointName,
        decimal latitude,
        decimal longitude,
        int? referenceStationId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var mountPoint = await dbContext.MountPoints
                .FirstOrDefaultAsync(m => m.Name == mountPointName, cancellationToken);

            if (mountPoint != null)
            {
                // Update with RTCM-extracted coordinates
                mountPoint.RtcmLatitude = latitude;
                mountPoint.RtcmLongitude = longitude;
                if (referenceStationId.HasValue)
                {
                    mountPoint.ReferenceStationId = referenceStationId.Value;
                }
                mountPoint.LastRtcmMessageTime = DateTime.UtcNow;
                mountPoint.MessageCount++;

                await dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogDebug(
                    "Updated MountPoint {MountPointName} with RTCM1005 position: {Lat},{Lon}",
                    mountPointName, latitude, longitude);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating MountPoint position for {MountPointName}", mountPointName);
        }
    }
}
