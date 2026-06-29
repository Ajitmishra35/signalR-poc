# Account Export POC - Hinglish Learning Guide

Ye guide junior developer ke point of view se likhi gayi hai. Goal hai samajhna ki async export flow kaise kaam karta hai using React, .NET 8, Azure Service Bus, Blob Storage, Table Storage, aur SignalR.

## Hum Kya Build Kar Rahe Hain?

Hum ek Account Summary Export application bana rahe hain.

User React UI me:

- `tenantId` enter karta hai
- `accountId` enter karta hai
- `Download Account Summary` button click karta hai

Lekin file turant generate nahi hoti. API immediately `202 Accepted` return karti hai, aur background me Worker CSV file generate karta hai. Jab file ready ho jati hai, React ko live SignalR notification milti hai aur download link show hota hai.

## Simple Flow

```text
React UI
  -> Export.Api
  -> Azure Service Bus Queue
  -> Export.Processor Worker
  -> Azure Blob Storage
  -> Azure Table Storage
  -> Azure Service Bus Topic
  -> Export.SignalRAdapter
  -> Azure SignalR Service
  -> React UI
```

## Step By Step Explanation

### 1. React UI Request Start Karta Hai

User React screen pe `Download Account Summary` click karta hai.

React API ko call karta hai:

```http
POST /api/exports/account-summary
```

Body:

```json
{
  "tenantId": "TENANT001",
  "accountId": "ACC1001"
}
```

React ka code:

```text
frontend/account-export-ui/src/services/exportApi.ts
```

## 2. API Immediately 202 Return Karti Hai

API file generate nahi karti. API sirf:

- request validate karti hai
- `exportId` create karti hai
- status ko `Requested` save karti hai
- Service Bus queue me message bhejti hai
- React ko `202 Accepted` return karti hai

Important: `202 Accepted` ka matlab hai request accept ho gayi, lekin processing abhi background me hogi.

API ka code:

```text
backend/src/Export.Api/Program.cs
```

API response example:

```json
{
  "exportId": "abc123",
  "tenantId": "TENANT001",
  "accountId": "ACC1001",
  "status": "Requested",
  "correlationId": "corr-123",
  "statusUrl": "/api/exports/abc123"
}
```

## 3. React SignalR Group Join Karta Hai

API se `exportId` milte hi React SignalR group join karta hai:

```text
export-{exportId}
```

Example:

```text
export-abc123
```

React SignalR code:

```text
frontend/account-export-ui/src/services/signalService.ts
```

Ye important hai because real system me `userId` available nahi ho sakta. Sirf `tenantId` available hota hai.

Isliye hum tenant group ko download URL broadcast nahi karte.

Wrong approach:

```text
tenant-TENANT001 group ko download URL bhejna
```

Problem: Same tenant ke dusre users bhi download URL dekh sakte hain.

Correct approach:

```text
export-{exportId} group ko download URL bhejna
```

Isse sirf wahi browser/client event receive karta hai jisne us export group ko join kiya.

## 4. Azure Service Bus Queue Ka Role

API ek message queue me send karti hai:

```text
account-summary-export-requests
```

Queue ka kaam hai API aur Processor ko decouple karna.

Matlab:

- API fast response de sakti hai
- Processor apne time pe message consume karta hai
- Agar processing slow hai, API block nahi hoti
- Agar Worker temporarily down hai, message queue me wait kar sakta hai

Message type:

```text
ExportRequestMessage
```

Shared contract file:

```text
backend/src/Export.Contracts/ExportContracts.cs
```

## 5. Export Processor Background Me Kaam Karta Hai

Processor ek Worker Service hai. Ye Service Bus queue se message consume karta hai.

Processor ka flow:

1. Message consume karo
2. Table Storage me status `Processing` update karo
3. 3 seconds fake delay
4. CSV content generate karo
5. Blob Storage me CSV upload karo
6. 30 minutes valid SAS download URL generate karo
7. Table Storage me status `Completed` update karo
8. Service Bus topic me `ExportStatusChanged` event publish karo

Processor ka code:

```text
backend/src/Export.Processor/AccountSummaryExportWorker.cs
```

## 6. Azure Blob Storage Ka Role

Generated CSV file Blob Storage me store hoti hai.

Blob path format:

```text
{tenantId}/{exportId}/account-summary-{accountId}.csv
```

Example:

```text
TENANT001/abc123/account-summary-ACC1001.csv
```

Blob Storage actual file rakhta hai.

## 7. SAS URL Kya Hota Hai?

SAS URL ek temporary download link hota hai.

Is POC me SAS URL:

- read-only hai
- 30 minutes valid hai
- direct CSV download ke liye use hota hai

React Blob Storage se directly baat nahi karta. React sirf final SAS URL open karta hai.

## 8. Azure Table Storage Ka Role

Table Storage export ka current status store karta hai.

Status values:

```text
Requested
Processing
Completed
Failed
```

Partition key:

```text
tenantId
```

Row key:

```text
exportId
```

Why?

- Same tenant ke exports ek partition me grouped hain
- Specific export lookup fast hai using tenantId + exportId

Status API fallback ke liye use hoti hai:

```http
GET /api/exports/{exportId}?tenantId=TENANT001
```

## 9. Processor SignalR Ko Direct Call Kyun Nahi Karta?

Processor ka responsibility hai:

- file generate karna
- blob upload karna
- status update karna
- event publish karna

Processor React ya SignalR ko directly call nahi karta.

Reason:

- clean separation
- Processor business work karta hai
- SignalR Adapter notification work karta hai
- Future me multiple consumers same event consume kar sakte hain

## 10. Service Bus Topic Ka Role

Processor jab export complete ya fail karta hai, tab topic me event publish karta hai:

```text
export-status-events
```

Event type:

```text
ExportStatusChangedEvent
```

Topic ka benefit:

- multiple subscribers ho sakte hain
- SignalR Adapter ek subscriber hai
- future me audit/email/webhook subscriber bhi add ho sakte hain

## 11. SignalR Adapter Ka Role

SignalR Adapter do kaam karta hai:

1. SignalR Hub host karta hai
2. Service Bus topic se export status events consume karta hai

Hub endpoint:

```text
http://localhost:5003/hubs/exports
```

SignalR Adapter event receive karne ke baad ye karta hai:

```text
Group("export-{exportId}").SendAsync("ExportStatusChanged", event)
```

Code:

```text
backend/src/Export.SignalRAdapter/ExportStatusEventSubscriber.cs
backend/src/Export.SignalRAdapter/ExportHub.cs
```

## 12. Azure SignalR Service Ka Role

Azure SignalR Service real-time connection manage karta hai.

React browser SignalR se connected hota hai. Jab backend event push karta hai, Azure SignalR message browser tak pahucha deta hai.

Is POC me local SignalR Adapter Azure SignalR Service ke through client ko notify karta hai.

## 13. React Toast Kab Dikhta Hai?

Jab SignalR se `ExportStatusChanged` event aata hai:

- agar status `Completed` hai, toast dikhta hai:

```text
URL generated - Click here
```

- agar status `Failed` hai, toast error message dikhata hai

UI code:

```text
frontend/account-export-ui/src/App.tsx
```

## 14. Failure Demo

Agar accountId:

```text
FAIL001
```

enter karte ho, Processor intentionally failure simulate karta hai.

Flow:

```text
Requested -> Processing -> Failed
```

React SignalR se failed event receive karta hai aur error show karta hai.

## 15. Important Files

```text
backend/src/Export.Contracts/ExportContracts.cs
```

Shared messages and table entity.

```text
backend/src/Export.Api/Program.cs
```

API endpoints, status create, Service Bus queue send.

```text
backend/src/Export.Processor/AccountSummaryExportWorker.cs
```

Queue consume, CSV generate, Blob upload, Table update, Topic publish.

```text
backend/src/Export.SignalRAdapter/ExportHub.cs
```

SignalR group join/leave methods.

```text
backend/src/Export.SignalRAdapter/ExportStatusEventSubscriber.cs
```

Topic consume and SignalR group push.

```text
frontend/account-export-ui/src/services/exportApi.ts
```

React API calls.

```text
frontend/account-export-ui/src/services/signalService.ts
```

React SignalR connection and group join.

```text
frontend/account-export-ui/src/App.tsx
```

Main UI, status timeline, logs, toast, download link.

## 16. How To Run

API:

```powershell
cd backend/src/Export.Api
dotnet run
```

Processor:

```powershell
cd backend/src/Export.Processor
dotnet run
```

SignalR Adapter:

```powershell
cd backend/src/Export.SignalRAdapter
dotnet run
```

React:

```powershell
cd frontend/account-export-ui
npm run dev
```

Open:

```text
http://localhost:5173
```

## 17. One Line Summary

API request ko accept karke queue me daalti hai, Processor background me file banata hai, Blob me upload karta hai, event publish karta hai, SignalR Adapter event ko React tak live push karta hai, aur React download link show karta hai.

