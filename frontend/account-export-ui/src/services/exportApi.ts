export type ExportStatus = 'Requested' | 'Processing' | 'Completed' | 'Failed';

export interface StartExportResponse {
  exportId: string;
  tenantId: string;
  accountId: string;
  status: ExportStatus;
  correlationId: string;
  statusUrl: string;
}

export interface ExportStatusResponse {
  exportId: string;
  tenantId: string;
  accountId: string;
  status: ExportStatus;
  downloadUrl?: string | null;
  errorMessage?: string | null;
  correlationId: string;
}

const apiBaseUrl = import.meta.env.VITE_EXPORT_API_BASE_URL ?? 'http://localhost:5001';

export async function startAccountSummaryExport(
  tenantId: string,
  accountId: string,
): Promise<StartExportResponse> {
  const response = await fetch(`${apiBaseUrl}/api/exports/account-summary`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'x-correlation-id': crypto.randomUUID(),
      'Idempotency-Key': crypto.randomUUID(),
    },
    body: JSON.stringify({ tenantId, accountId }),
  });

  if (!response.ok) {
    throw new Error(await readError(response));
  }

  return response.json();
}

export async function getExportStatus(
  tenantId: string,
  exportId: string,
): Promise<ExportStatusResponse> {
  const params = new URLSearchParams({ tenantId });
  const response = await fetch(`${apiBaseUrl}/api/exports/${exportId}?${params}`);

  if (!response.ok) {
    throw new Error(await readError(response));
  }

  return response.json();
}

async function readError(response: Response): Promise<string> {
  try {
    const payload = await response.json();
    return payload.error ?? `Request failed with HTTP ${response.status}`;
  } catch {
    return `Request failed with HTTP ${response.status}`;
  }
}
