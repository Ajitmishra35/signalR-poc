import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import type { ExportStatusResponse } from './exportApi';

let connection: HubConnection | null = null;
let startPromise: Promise<void> | null = null;

const hubUrl = import.meta.env.VITE_SIGNALR_HUB_URL ?? 'http://localhost:5003/hubs/exports';

export async function startConnection(): Promise<void> {
  const activeConnection = getOrCreateConnection();

  if (activeConnection.state === HubConnectionState.Connected) {
    return;
  }

  if (
    activeConnection.state === HubConnectionState.Connecting ||
    activeConnection.state === HubConnectionState.Reconnecting
  ) {
    await startPromise;
    return;
  }

  startPromise = activeConnection
    .start()
    .then(() => {
      console.log('SignalR connected', activeConnection.connectionId);
    })
    .finally(() => {
      startPromise = null;
    });

  await startPromise;
}

function getOrCreateConnection(): HubConnection {
  if (connection) {
    return connection;
  }

  connection = new HubConnectionBuilder()
    .withUrl(hubUrl)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Information)
    .build();

  connection.onreconnecting((error) => {
    console.log('SignalR reconnecting', error);
  });

  connection.onreconnected((connectionId) => {
    console.log('SignalR reconnected', connectionId);
  });

  connection.onclose((error) => {
    console.log('SignalR closed', error);
  });

  return connection;
}

export async function joinExportGroup(exportId: string): Promise<void> {
  await startConnection();
  await requireConnection().invoke('JoinExportGroup', exportId);
}

export async function leaveExportGroup(exportId: string): Promise<void> {
  await startConnection();
  await requireConnection().invoke('LeaveExportGroup', exportId);
}

export function onExportStatusChanged(
  callback: (event: ExportStatusResponse) => void,
): () => void {
  const activeConnection = getOrCreateConnection();
  activeConnection.on('ExportStatusChanged', callback);
  return () => activeConnection.off('ExportStatusChanged', callback);
}

export async function stopConnection(): Promise<void> {
  if (connection) {
    await connection.stop();
  }
}

export function getConnectionState(): HubConnectionState | 'NotStarted' {
  return connection?.state ?? 'NotStarted';
}

function requireConnection(): HubConnection {
  if (!connection) {
    throw new Error('SignalR connection has not been started.');
  }

  return connection;
}
