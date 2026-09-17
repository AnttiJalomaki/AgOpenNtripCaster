import React, { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import DashboardLayout from '../../components/Layout/DashboardLayout';
import {
  diagnosticsApi,
  type DiagnosticEvent,
  type DiagnosticSessionDetail,
  type DiagnosticSessionSummary,
} from '../../services/diagnosticsApi';
import styles from './DiagnosticSessionPage.module.css';

const formatDateTime = (value?: string) => {
  if (!value) return 'N/A';
  return new Date(value).toLocaleString();
};

const formatBytes = (bytes: number) => {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
};

const formatSeconds = (seconds: number) => {
  if (seconds < 60) return `${seconds.toFixed(0)}s`;
  if (seconds < 3600) return `${(seconds / 60).toFixed(1)}m`;
  return `${(seconds / 3600).toFixed(1)}h`;
};

const formatNumber = (value?: number, digits = 2) => (value == null ? 'N/A' : value.toFixed(digits));

const formatJson = (value?: string) => {
  if (!value) return '';
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
};

const severityClassName = (severity: string) => {
  switch (severity.toLowerCase()) {
    case 'error':
      return `${styles.badge} ${styles.badgeError}`;
    case 'warning':
      return `${styles.badge} ${styles.badgeWarning}`;
    case 'debug':
      return `${styles.badge} ${styles.badgeMuted}`;
    default:
      return `${styles.badge} ${styles.badgeInfo}`;
  }
};

const roverLabel = (session: DiagnosticSessionSummary) =>
  `${session.userName || 'unknown'} #${session.serialNumber}`;

export const DiagnosticSessionPage: React.FC = () => {
  const { sessionId } = useParams<{ sessionId: string }>();
  const [detail, setDetail] = useState<DiagnosticSessionDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const loadSession = async () => {
      if (!sessionId) {
        setError('Missing session id');
        setLoading(false);
        return;
      }

      try {
        setError(null);
        const data = await diagnosticsApi.getSession(sessionId);
        setDetail(data);
      } catch (err) {
        console.error('Failed to load diagnostic session:', err);
        setError('Failed to load diagnostic session');
      } finally {
        setLoading(false);
      }
    };

    loadSession();
  }, [sessionId]);

  const session = detail?.session;

  return (
    <DashboardLayout>
      <div className={styles.container}>
        <div className={styles.header}>
          <div>
            <Link className={styles.backLink} to="/admin/diagnostics">Back to diagnostics</Link>
            <h1 className={styles.title}>Rover Session</h1>
            <p className={styles.subtitle}>{session ? roverLabel(session) : sessionId}</p>
          </div>
        </div>

        {error && <div className={styles.error}>{error}</div>}

        {loading && !detail ? (
          <div className={styles.loading}>Loading session...</div>
        ) : session ? (
          <>
            <div className={styles.statsGrid}>
              <div className={styles.statCard}>
                <div className={styles.statLabel}>Rover</div>
                <div className={styles.statValue}>{roverLabel(session)}</div>
                <div className={styles.statMeta}>{session.clientIpAddress || 'no IP address'}</div>
              </div>
              <div className={styles.statCard}>
                <div className={styles.statLabel}>Mount Point</div>
                <div className={styles.statValue}>{session.mountPointName || session.mountPointId}</div>
                <div className={styles.statMeta}>{session.status}</div>
              </div>
              <div className={styles.statCard}>
                <div className={styles.statLabel}>Connected</div>
                <div className={styles.statValueSmall}>{formatDateTime(session.connectedAt)}</div>
                <div className={styles.statMeta}>{session.disconnectedAt ? formatDateTime(session.disconnectedAt) : 'active'}</div>
              </div>
              <div className={styles.statCard}>
                <div className={styles.statLabel}>Traffic</div>
                <div className={styles.statValueSmall}>{formatBytes(session.bytesSent)} sent</div>
                <div className={styles.statMeta}>{formatBytes(session.bytesReceived)} received</div>
              </div>
            </div>

            <section className={styles.section}>
              <div className={styles.sectionHeader}>
                <h2 className={styles.sectionTitle}>Position And GGA</h2>
              </div>
              <div className={styles.metricsGrid}>
                <Metric label="First GGA" value={formatDateTime(session.firstGgaAt)} />
                <Metric label="Last Position" value={formatDateTime(session.lastPositionAt)} />
                <Metric label="Latitude" value={formatNumber(session.lastLatitude, 6)} />
                <Metric label="Longitude" value={formatNumber(session.lastLongitude, 6)} />
                <Metric label="Accuracy" value={formatNumber(session.lastAccuracy, 2)} />
                <Metric label="GGA Frames" value={session.ggaFrameCount.toString()} />
                <Metric label="Invalid GGA" value={session.invalidGgaFrameCount.toString()} />
                <Metric label="Fix Quality" value={session.lastFixQuality?.toString() || 'N/A'} />
                <Metric label="Satellites" value={session.lastSatelliteCount?.toString() || 'N/A'} />
                <Metric label="HDOP" value={formatNumber(session.lastHdop, 2)} />
                <Metric label="Altitude" value={formatNumber(session.lastAltitudeMeters, 2)} />
                <Metric label="Correction Age" value={formatNumber(session.lastDifferentialAgeSeconds, 1)} />
              </div>
            </section>

            <section className={styles.section}>
              <div className={styles.sectionHeader}>
                <h2 className={styles.sectionTitle}>Stream Diagnostics</h2>
              </div>
              <div className={styles.metricsGrid}>
                <Metric label="Stale Periods" value={session.stalePositionPeriods.toString()} />
                <Metric label="Paused Time" value={formatSeconds(session.streamPausedSeconds)} />
                <Metric label="Peak Pending Buffers" value={session.peakPendingBufferCount.toString()} />
                <Metric label="Disconnect Reason" value={session.disconnectReason || 'N/A'} />
                <Metric label="User Agent" value={session.clientUserAgent || 'N/A'} wide />
                <Metric label="Station ID" value={session.lastDifferentialStationId || 'N/A'} />
              </div>
            </section>

            <section className={styles.section}>
              <div className={styles.sectionHeader}>
                <h2 className={styles.sectionTitle}>Timeline</h2>
              </div>
              <div className={styles.timeline}>
                {detail.events.length === 0 ? (
                  <div className={styles.emptyState}>No diagnostic events recorded for this session</div>
                ) : detail.events.map((event: DiagnosticEvent) => (
                  <div key={event.id} className={styles.eventRow}>
                    <div className={styles.eventTime}>{formatDateTime(event.timestamp)}</div>
                    <div className={severityClassName(event.severity)}>{event.severity}</div>
                    <div className={styles.eventBody}>
                      <div className={styles.eventTitle}>{event.kind}</div>
                      <div className={styles.eventMessage}>{event.message}</div>
                      {event.dataJson && (
                        <pre className={styles.eventData}>{formatJson(event.dataJson)}</pre>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            </section>
          </>
        ) : null}
      </div>
    </DashboardLayout>
  );
};

interface MetricProps {
  label: string;
  value: string;
  wide?: boolean;
}

const Metric: React.FC<MetricProps> = ({ label, value, wide = false }) => (
  <div className={`${styles.metric} ${wide ? styles.metricWide : ''}`}>
    <div className={styles.metricLabel}>{label}</div>
    <div className={styles.metricValue}>{value}</div>
  </div>
);

export default DiagnosticSessionPage;
