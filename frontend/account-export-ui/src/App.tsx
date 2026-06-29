import { useEffect, useMemo, useState } from 'react';
import './App.css';
import {
  ExportStatus,
  ExportStatusResponse,
  getExportStatus,
  startAccountSummaryExport,
} from './services/exportApi';
import {
  getConnectionState,
  joinExportGroup,
  onExportStatusChanged,
  startConnection,
  stopConnection,
} from './services/signalService';

const timeline: ExportStatus[] = ['Requested', 'Processing', 'Completed', 'Failed'];

function App() {
  const [tenantId, setTenantId] = useState('TENANT001');
  const [accountId, setAccountId] = useState('ACC1001');
  const [latest, setLatest] = useState<ExportStatusResponse | null>(null);
  const [connectionState, setConnectionState] = useState(String(getConnectionState()));
  const [isStarting, setIsStarting] = useState(false);
  const [logs, setLogs] = useState<string[]>([]);
  const [toast, setToast] = useState<ExportStatusResponse | null>(null);

  const addLog = (message: string) => {
    const stamp = new Date().toLocaleTimeString();
    setLogs((current) => [`${stamp} ${message}`, ...current].slice(0, 30));
  };

  useEffect(() => {
    let unsubscribe: (() => void) | undefined;
    let cancelled = false;

    unsubscribe = onExportStatusChanged((event) => {
      setLatest(event);
      setToast(event);
      addLog(`SignalR ExportStatusChanged event received: ${event.status}`);

      if (event.status === 'Completed') {
        addLog('Download ready');
      }
    });

    startConnection()
      .then(() => {
        if (cancelled) {
          return;
        }

        setConnectionState(String(getConnectionState()));
        addLog('SignalR connected');
      })
      .catch((error) => {
        setConnectionState(String(getConnectionState()));
        addLog(`SignalR connection failed: ${error.message}`);
      });

    const interval = window.setInterval(() => {
      setConnectionState(String(getConnectionState()));
    }, 1000);

    return () => {
      cancelled = true;
      window.clearInterval(interval);
      unsubscribe?.();
      void stopConnection();
    };
  }, []);

  const activeStep = useMemo(() => latest?.status ?? 'Requested', [latest?.status]);

  async function startExport() {
    setIsStarting(true);
    setLatest(null);

    try {
      await startConnection();
      setConnectionState(String(getConnectionState()));

      const accepted = await startAccountSummaryExport(tenantId.trim(), accountId.trim());
      addLog(`API 202 received for export ${accepted.exportId}`);

      // React joins an export-specific group because tenantId alone is too broad
      // for sensitive download URLs when userId is unavailable.
      await joinExportGroup(accepted.exportId);
      addLog(`Joined export group export-${accepted.exportId}`);

      setLatest({
        exportId: accepted.exportId,
        tenantId: accepted.tenantId,
        accountId: accepted.accountId,
        status: accepted.status,
        correlationId: accepted.correlationId,
      });
    } catch (error) {
      addLog(error instanceof Error ? error.message : 'Export request failed');
    } finally {
      setIsStarting(false);
    }
  }

  async function checkStatus() {
    if (!latest?.exportId) {
      addLog('No exportId yet. Start an export first.');
      return;
    }

    try {
      // Status API is only a fallback if a live SignalR event is missed.
      const status = await getExportStatus(tenantId.trim(), latest.exportId);
      setLatest(status);
      addLog(`Manual status checked: ${status.status}`);
    } catch (error) {
      addLog(error instanceof Error ? error.message : 'Manual status check failed');
    }
  }

  return (
    <main className="app-shell">
      {toast && (
        <div className={`toast ${toast.status === 'Failed' ? 'toast-failed' : ''}`}>
          <button className="toast-close" onClick={() => setToast(null)} aria-label="Close toast">
            x
          </button>
          <strong>
            {toast.status === 'Completed'
              ? 'URL generated'
              : toast.status === 'Failed'
                ? 'Export failed'
                : `Export ${toast.status}`}
          </strong>
          <span>
            {toast.status === 'Completed' && toast.downloadUrl ? (
              <>
                Download is ready. <a href={toast.downloadUrl}>Click here</a>
              </>
            ) : (
              toast.errorMessage ?? `Status changed to ${toast.status}`
            )}
          </span>
        </div>
      )}

      <section className="workspace">
        <div className="header-row">
          <div>
            <p className="eyebrow">Async Account Summary Export</p>
            <h1>Export console</h1>
          </div>
          <div className={`connection ${connectionState === 'Connected' ? 'connected' : ''}`}>
            <span />
            {connectionState}
          </div>
        </div>

        <div className="control-grid">
          <label>
            Tenant ID
            <input value={tenantId} onChange={(event) => setTenantId(event.target.value)} />
          </label>
          <label>
            Account ID
            <input value={accountId} onChange={(event) => setAccountId(event.target.value)} />
          </label>
          <button onClick={startExport} disabled={isStarting}>
            {isStarting ? 'Requesting...' : 'Download Account Summary'}
          </button>
          <button className="secondary" onClick={checkStatus}>
            Check Status
          </button>
        </div>

        <section className="status-panel">
          <div className="status-grid">
            <Detail label="Status" value={latest?.status ?? 'Not requested'} />
            <Detail label="Tenant ID" value={latest?.tenantId ?? tenantId} />
            <Detail label="Account ID" value={latest?.accountId ?? accountId} />
            <Detail label="Export ID" value={latest?.exportId ?? '-'} />
            <Detail label="Correlation ID" value={latest?.correlationId ?? '-'} />
          </div>

          <div className="timeline">
            {timeline.map((step) => {
              const isFailedStep = activeStep === 'Failed' && step === 'Failed';
              const isActive =
                step === activeStep ||
                (activeStep === 'Completed' && ['Requested', 'Processing'].includes(step));

              return (
                <div
                  key={step}
                  className={`timeline-step ${isActive ? 'active' : ''} ${isFailedStep ? 'failed' : ''}`}
                >
                  <span />
                  {step}
                </div>
              );
            })}
          </div>

          {latest?.status === 'Failed' && (
            <div className="error-box">{latest.errorMessage ?? 'Export failed.'}</div>
          )}

          {latest?.status === 'Completed' && latest.downloadUrl && (
            <a className="download-link" href={latest.downloadUrl}>
              URL generated - Click here
            </a>
          )}
        </section>
      </section>

      <aside className="logs-panel">
        <h2>Logs</h2>
        <div className="logs">
          {logs.length === 0 ? (
            <p className="muted">Waiting for activity</p>
          ) : (
            logs.map((log) => <p key={log}>{log}</p>)
          )}
        </div>
      </aside>
    </main>
  );
}

function Detail({ label, value }: { label: string; value: string }) {
  return (
    <div className="detail">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

export default App;
