import React, { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import DashboardLayout from '../../components/Layout/DashboardLayout';
import {
  diagnosticsApi,
  type DiagnosticEvent,
  type DiagnosticSessionSummary,
  type DiagnosticsOverview,
} from '../../services/diagnosticsApi';
import styles from './DiagnosticsPage.module.css';

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

const formatCoordinate = (value?: number) => (value == null ? 'N/A' : value.toFixed(6));

const sessionLabel = (session: DiagnosticSessionSummary) =>
  `${session.userName || 'unknown'} #${session.serialNumber}`;

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

const issueCount = (session: DiagnosticSessionSummary) =>
  session.invalidGgaFrameCount + session.stalePositionPeriods;

export const DiagnosticsPage: React.FC = () => {
  const [overview, setOverview] = useState<DiagnosticsOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const loadOverview = async (isRefresh = false) => {
    try {
      if (isRefresh) setRefreshing(true);
      setError(null);
      const data = await diagnosticsApi.getOverview();
      setOverview(data);
    } catch (err) {
      console.error('Failed to load diagnostics overview:', err);
      setError('Failed to load diagnostics');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  };

  useEffect(() => {
    loadOverview();
  }, []);

  return (
    <DashboardLayout>
      <div className={styles.container}>
        <div className={styles.header}>
          <div>
            <h1 className={styles.title}>Rover Diagnostics</h1>
            <p className={styles.subtitle}>Passive NTRIP session health and rover/base irregularities</p>
          </div>
          <button
            type="button"
            className={styles.refreshButton}
            onClick={() => loadOverview(true)}
            disabled={refreshing}
          >
            {refreshing ? 'Refreshing...' : 'Refresh'}
          </button>
        </div>

        {error && <div className={styles.error}>{error}</div>}

        {loading && !overview ? (
          <div className={styles.loading}>Loading diagnostics...</div>
        ) : overview ? (
          <>
            <div className={styles.statsGrid}>
              <div className={styles.statCard}>
                <div className={styles.statLabel}>Active Rovers</div>
                <div className={styles.statValue}>{overview.activeRovers}</div>
                <div className={styles.statMeta}>{overview.staleRovers} stale</div>
              </div>
              <div className={styles.statCard}>
                <div className={styles.statLabel}>Active Bases</div>
                <div className={styles.statValue}>{overview.activeSources}</div>
                <div className={styles.statMeta}>source connections</div>
              </div>
              <div className={styles.statCard}>
                <div className={styles.statLabel}>Mount Points</div>
                <div className={styles.statValue}>{overview.mountPoints.length}</div>
                <div className={styles.statMeta}>configured streams</div>
              </div>
              <div className={styles.statCard}>
                <div className={styles.statLabel}>Last RTCM</div>
                <div className={styles.statValueSmall}>{formatDateTime(overview.lastRtcmAt)}</div>
                <div className={styles.statMeta}>latest base activity</div>
              </div>
            </div>

            <section className={styles.section}>
              <div className={styles.sectionHeader}>
                <h2 className={styles.sectionTitle}>Mount Point Health</h2>
              </div>
              <div className={styles.tableWrap}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th>Mount Point</th>
                      <th>Base</th>
                      <th>Rovers</th>
                      <th>Last RTCM</th>
                      <th>Coordinates</th>
                      <th>Source Starts 24h</th>
                    </tr>
                  </thead>
                  <tbody>
                    {overview.mountPoints.map((mountPoint) => (
                      <tr key={mountPoint.mountPointId}>
                        <td className={styles.strongCell}>{mountPoint.name}</td>
                        <td>
                          <span className={mountPoint.sourceOnline ? styles.statusOnline : styles.statusOffline}>
                            {mountPoint.sourceOnline ? 'Online' : 'Offline'}
                          </span>
                        </td>
                        <td>{mountPoint.activeRovers}</td>
                        <td>{formatDateTime(mountPoint.lastRtcmAt)}</td>
                        <td>
                          {formatCoordinate(mountPoint.rtcmLatitude)}, {formatCoordinate(mountPoint.rtcmLongitude)}
                        </td>
                        <td>{mountPoint.sourceReconnects24h}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>

            <section className={styles.section}>
              <div className={styles.sectionHeader}>
                <h2 className={styles.sectionTitle}>Recent Rover Sessions</h2>
              </div>
              <div className={styles.tableWrap}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th>Rover</th>
                      <th>Mount Point</th>
                      <th>Connected</th>
                      <th>Status</th>
                      <th>Traffic</th>
                      <th>GGA</th>
                      <th>Issues</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    {overview.recentSessions.length === 0 ? (
                      <tr>
                        <td colSpan={8} className={styles.emptyCell}>No rover sessions recorded</td>
                      </tr>
                    ) : overview.recentSessions.map((session) => (
                      <tr key={session.id}>
                        <td>
                          <div className={styles.strongCell}>{sessionLabel(session)}</div>
                          <div className={styles.mutedCell}>{session.clientIpAddress || 'no IP'}</div>
                        </td>
                        <td>{session.mountPointName || session.mountPointId}</td>
                        <td>
                          <div>{formatDateTime(session.connectedAt)}</div>
                          <div className={styles.mutedCell}>{session.disconnectedAt ? formatDateTime(session.disconnectedAt) : 'active'}</div>
                        </td>
                        <td><span className={styles.statusNeutral}>{session.status}</span></td>
                        <td>
                          <div>{formatBytes(session.bytesSent)} sent</div>
                          <div className={styles.mutedCell}>{formatBytes(session.bytesReceived)} received</div>
                        </td>
                        <td>
                          <div>{session.ggaFrameCount} frames</div>
                          <div className={styles.mutedCell}>
                            fix {session.lastFixQuality ?? 'N/A'}, sats {session.lastSatelliteCount ?? 'N/A'}
                          </div>
                        </td>
                        <td>
                          <span className={issueCount(session) > 0 ? styles.issueBadge : styles.okBadge}>
                            {issueCount(session)}
                          </span>
                        </td>
                        <td>
                          <Link className={styles.linkButton} to={`/admin/diagnostics/sessions/${session.id}`}>
                            Details
                          </Link>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>

            <section className={styles.section}>
              <div className={styles.sectionHeader}>
                <h2 className={styles.sectionTitle}>Recent Diagnostic Events</h2>
              </div>
              <div className={styles.eventsList}>
                {overview.recentEvents.length === 0 ? (
                  <div className={styles.emptyState}>No diagnostic events recorded</div>
                ) : overview.recentEvents.map((event: DiagnosticEvent) => (
                  <div key={event.id} className={styles.eventRow}>
                    <div className={styles.eventTime}>{formatDateTime(event.timestamp)}</div>
                    <div className={severityClassName(event.severity)}>{event.severity}</div>
                    <div className={styles.eventBody}>
                      <div className={styles.eventTitle}>{event.kind}</div>
                      <div className={styles.eventMessage}>{event.message}</div>
                      <div className={styles.eventMeta}>
                        {event.userName || event.userId || 'system'} - {event.mountPointName || 'no mount point'}
                      </div>
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

export default DiagnosticsPage;
