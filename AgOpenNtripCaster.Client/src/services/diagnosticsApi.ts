import api from './api';

export interface DiagnosticEvent {
  id: number;
  timestamp: string;
  severity: string;
  kind: string;
  userId?: string;
  userName?: string;
  clientSessionId?: string;
  sourceConnectionId?: number;
  mountPointId?: number;
  mountPointName?: string;
  message: string;
  dataJson?: string;
}

export interface DiagnosticMountPointSummary {
  mountPointId: number;
  name: string;
  sourceOnline: boolean;
  activeRovers: number;
  lastRtcmAt?: string;
  sourceReconnects24h: number;
  rtcmLatitude?: number;
  rtcmLongitude?: number;
}

export interface DiagnosticSessionSummary {
  id: string;
  userId?: string;
  userName?: string;
  clientIpAddress?: string;
  clientUserAgent?: string;
  mountPointId: number;
  mountPointName?: string;
  serialNumber: number;
  connectedAt: string;
  disconnectedAt?: string;
  status: string;
  disconnectReason?: string;
  bytesSent: number;
  bytesReceived: number;
  lastPositionAt?: string;
  lastLatitude?: number;
  lastLongitude?: number;
  lastAccuracy?: number;
  firstGgaAt?: string;
  ggaFrameCount: number;
  invalidGgaFrameCount: number;
  lastFixQuality?: number;
  lastSatelliteCount?: number;
  lastHdop?: number;
  lastAltitudeMeters?: number;
  lastGeoidSeparationMeters?: number;
  lastDifferentialAgeSeconds?: number;
  lastDifferentialStationId?: string;
  stalePositionPeriods: number;
  streamPausedSeconds: number;
  peakPendingBufferCount: number;
}

export interface DiagnosticsOverview {
  activeRovers: number;
  activeSources: number;
  staleRovers: number;
  lastRtcmAt?: string;
  mountPoints: DiagnosticMountPointSummary[];
  recentSessions: DiagnosticSessionSummary[];
  recentEvents: DiagnosticEvent[];
}

export interface DiagnosticSessionDetail {
  session: DiagnosticSessionSummary;
  events: DiagnosticEvent[];
}

export interface DiagnosticEventQuery {
  since?: string;
  until?: string;
  userId?: string;
  clientSessionId?: string;
  sourceConnectionId?: number;
  mountPointId?: number;
  severity?: string;
  kind?: string;
  limit?: number;
}

export const diagnosticsApi = {
  async getOverview(): Promise<DiagnosticsOverview> {
    const response = await api.get<DiagnosticsOverview>('/admin/diagnostics/overview');
    return response.data;
  },

  async getSession(sessionId: string): Promise<DiagnosticSessionDetail> {
    const response = await api.get<DiagnosticSessionDetail>(`/admin/diagnostics/sessions/${sessionId}`);
    return response.data;
  },

  async getEvents(query: DiagnosticEventQuery = {}): Promise<DiagnosticEvent[]> {
    const response = await api.get<DiagnosticEvent[]>('/admin/diagnostics/events', {
      params: query,
    });
    return response.data;
  },
};
