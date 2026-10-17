# Worklane — Development Log

> บันทึกสถานะโปรเจค ว่าตอนนี้ไปถึงไหน ทำอะไรไปแล้วบ้าง และอะไรยังขาด
> อัปเดตล่าสุด: **2026-10-17**

---

## 1. ภาพรวม

Worklane = ระบบจัดการงานแบบ multi-tenant (Jira ฉบับย่อ) แยกเป็น **2 git repository**:

| Repo | Path | Branch | Commit ล่าสุด |
|---|---|---|---|
| Backend | `C:\dev\worklane` | `develop` | `1da19ce` (2026-08-23) — commit ไว้ในเครื่อง ยังไม่ได้ push (ดูการ์ด WL-39) |
| Frontend | `C:\dev\worklane\web` | `develop` | `2e8f2be` (2026-08-23) — commit ไว้ในเครื่อง ยังไม่ได้ push (ดูการ์ด WL-39) |

> ⚠️ `web/` ถูก ignore ไว้ใน repo หลัก (`.gitignore` บรรทัดที่ 2) เพราะเป็น repo แยกชื่อ `Worklane-Web`
> — เวลาสั่ง `git status` ที่ root **จะไม่เห็นงานฝั่ง frontend เลย** ต้อง `cd web` ก่อน

**Stack:** ASP.NET Core 10 (Clean Architecture) + EF Core/PostgreSQL + Identity/JWT · Angular 22 standalone + Bootstrap 5 + ng-bootstrap + ngx-translate (en/th)

---

## 2. Timeline

| วันที่ | เหตุการณ์ |
|---|---|
| 2026-07-14 | **Phase 1** — วาง Clean Architecture, Auth (JWT + refresh token), Workspace, Member/Invitation, Project, multi-tenancy filter |
| 2026-07-15 | **Phase 2 (Backend)** — BoardColumn / TaskItem / ProjectTaskCounter, BoardService, ColumnsController, TasksController, migration `AddBoardEntities` → commit `eff24c7` |
| 2026-07-15 | **Phase 2 (Frontend)** — เขียนหน้า Board + drag-drop + redesign ธีมทั้งแอป → **ยังไม่ได้ commit** |
| 2026-07-15 → 2026-08-22 | หยุดพัฒนา ~5 สัปดาห์ |
| 2026-08-22 | กลับมารันโปรเจค, audit สถานะ, **เจอและแก้บั๊ก 3 จุด** → `de065da`, สร้างบอร์ด `WL` ติดตามงานตัวเอง |
| 2026-08-22 | **commit งาน frontend ที่ค้าง 5 สัปดาห์** (31 ไฟล์) → `734512f` |
| 2026-08-22 | **Phase 3 — วัดผลเป็นแมนเดย์** → `482b346` (backend) + `d97b4f4` (frontend) |
| 2026-08-23 | **WL-18 — SignalR broadcast** (สร้าง/ย้าย/แก้การ์ด) → `a09b9f3` (backend) + `3911c93` (frontend), ยังไม่ได้ push |
| 2026-08-23 | **WL-19 — Presence** (ใครออนไลน์ + badge ใครกำลังแก้การ์ด) → `eb99353` (backend) + `d20884e` (frontend), ยังไม่ได้ push |
| 2026-08-23 | 🐛 แก้บั๊ก quick-add ซ้ำ/มองไม่เห็น + badge จำนวนการ์ดสีชนกัน (WL-51, WL-52) → `96af1a8`, `74feb15` (frontend) |
| 2026-08-23 | **WL-24 — ส่งอีเมลเชิญจริง** (Gmail SMTP) + สร้างหน้ารับคำเชิญที่หายไป → `a27c99b` (backend) + `1ee4b24` (frontend), ยังไม่ได้ push |
| 2026-08-23 | **WL-21 — หน้า "งานของฉัน" ข้ามโปรเจค** → `1da19ce` (backend) + `2e8f2be` (frontend), ยังไม่ได้ push |
| 2026-10-17 | **Activity log, comments, WIP limit, attachments** (backend) — migration `AddCommentsAttachmentsActivityAndWip` · ตัดสินใจ WL-35: งานวันเสาร์-อาทิตย์นับ 0 วันทำการ |

---

## 3. สิ่งที่ทำเสร็จแล้ว

### Backend — เสร็จและมีเทสต์คุม

**Domain (12 entities):** `Workspace` `WorkspaceMember` `Invitation` `Project` `ProjectMember` `BoardColumn` `TaskItem` `TaskActivity` `TaskComment` `TaskAttachment` `ProjectTaskCounter` `RefreshToken`

**API endpoints ทั้งหมด 36 เส้น:**

| กลุ่ม | Endpoints |
|---|---|
| Auth | `POST /api/auth/register` `login` `refresh` `logout` |
| Workspaces | `GET/POST /api/workspaces` · `GET {id}/members` · `POST {id}/invitations` · `PATCH/DELETE {id}/members/{userId}` |
| Invitations | `POST /api/invitations/accept` |
| Projects | `GET/POST /api/workspaces/{id}/projects` · `PATCH/DELETE /api/projects/{id}` |
| Columns | `GET/POST /api/projects/{projectId}/columns` · `PATCH/DELETE /api/columns/{columnId}` |
| Tasks | `GET/POST /api/projects/{projectId}/tasks` · `GET/PATCH/DELETE /api/tasks/{taskId}` · `POST /api/tasks/{taskId}/move` · `GET /api/workspaces/{id}/my-tasks` |
| Activity | `GET /api/tasks/{taskId}/activity` — ใหม่สุดก่อน |
| Comments | `GET/POST /api/tasks/{taskId}/comments` · `PATCH/DELETE /api/comments/{commentId}` |
| Attachments | `GET/POST /api/tasks/{taskId}/attachments` · `GET /api/attachments/{attachmentId}/download` · `DELETE /api/attachments/{attachmentId}` |
| Reports | `GET /api/workspaces/{id}/reports/members` |
| Realtime | SignalR hub `/hubs/notifications` — `JoinProject`/`LeaveProject`, broadcast `TaskCreated`/`TaskMoved`/`TaskUpdated`, `CommentCreated`/`CommentUpdated`/`CommentDeleted`, `AttachmentAdded`/`AttachmentDeleted` ต่อกลุ่ม `project:{id}`; presence (`UserOnline`/`UserOffline`) + `Start`/`StopEditingTask` |

**จุดที่ออกแบบไว้ดี:**
- **Multi-tenancy 2 ชั้น** — `[RequireWorkspaceRole]` อ่าน `WorkspaceId` จาก *route* เท่านั้น (ไม่เชื่อ body) + EF global query filter กันข้อมูลข้าม tenant ซ้ำอีกชั้น
- **Fractional ordering** — การ์ดใช้ `Order` เป็น `double` แล้วหาค่ากึ่งกลางตอนลาก ไม่ต้อง UPDATE ทั้งคอลัมน์
- **Task number แบบ atomic** — `INSERT ... ON CONFLICT DO UPDATE RETURNING` กันเลขซ้ำตอนสร้างพร้อมกัน (`DEMO-1`, `DEMO-2`, ...)
- **จับเวลาโดยไม่ต้องกรอก** — ลากการ์ดเข้าคอลัมน์ที่ตั้ง `IsInProgress` แล้วระบบ stamp เวลาให้เอง
  `TaskActivity` เก็บประวัติทุกการย้าย เผื่อการ์ดถูกลากออกจาก Done แล้วกลับเข้าใหม่
- **อีเมลเชิญจริงผ่าน Gmail SMTP** — `IEmailService` ส่งไม่สำเร็จก็แค่ log warning ไม่ทำให้สร้างคำเชิญล้มเหลว, credential เก็บใน user-secrets เท่านั้น
- **Activity log** — `TaskActivity` เดิมเก็บบันทึกการสร้าง/ย้ายคอลัมน์ ตอนนี้มี type + details และครอบคลุมการแก้ฟิลด์, คอมเมนต์, ไฟล์แนบ
- **คอมเมนต์** — สร้าง/ดู/แก้ (เจ้าของเท่านั้น)/ลบ (เจ้าของ หรือ admin/owner) แล้ว broadcast ผ่าน SignalR
- **WIP limit แบบ soft** — ตั้งบนคอลัมน์ได้, เกินแล้วยังย้าย/สร้างการ์ดได้ แต่ response บอก `isOverWipLimit`
- **ไฟล์แนบ** — อัปโหลดผ่าน `IFileStorage` (ดิสก์ในเครื่อง, สลับ S3/Blob ได้ทีหลัง), จำกัดขนาดและชนิดไฟล์, ดาวน์โหลดเช็ค tenant เหมือน endpoint อื่น
- **จำนวนคอมเมนต์/ไฟล์บนการ์ด** — `commentCount` และ `attachmentCount` อยู่ใน task DTO ทุกเส้น (get, list, my-tasks, การ์ดในคอลัมน์, create/move/update) นับด้วยสอง grouped query ต่อชุดผลลัพธ์ ไม่ยิงทีละการ์ด และ broadcast `TaskUpdated` เมื่อคอมเมนต์หรือไฟล์เปลี่ยน
- **คอลัมน์ต้องอยู่โปรเจกต์เดียวกัน** — create/move/PATCH ที่ชี้คอลัมน์คนละโปรเจกต์หรือคนละ workspace ได้ 404 และไม่เก็บชื่อคอลัมน์นั้นใน activity; `BoardColumn` มี tenant filter
- **ลบคอมเมนต์แล้วข้อความหายจาก activity** — แถว CommentAdded/CommentEdited ถูกลบ `body`/`oldValue`/`newValue`
- **ไฟล์แนบ** — ตรวจนามสกุลและ magic bytes ไม่เชื่อ Content-Type จาก client, ดาวน์โหลดเป็น `application/octet-stream` พร้อม `nosniff` และ CSP, Kestrel จำกัดเฉพาะเส้นอัปโหลดที่ ~11 MB ส่วน endpoint อื่นคง 30 MB
- **สมาชิกปัจจุบันเท่านั้นที่เข้ากลุ่ม SignalR** — `JoinProject` เช็คสมาชิก workspace, ถอดสมาชิกแล้วตัดการเชื่อมต่อออกจาก `project:{id}`, และเปิด `CloseOnAuthenticationExpiration`
- **tenant filter ปิดเมื่อไม่มี workspace** — query ที่ยังไม่ปัก tenant ไม่คืนแถว; assignee ต้องเป็นสมาชิก workspace; 500 ไม่ส่ง `exception.Message`
- **แมนเดย์วันหยุดสุดสัปดาห์ = 0** (WL-35) — คงกฎเดิม ไม่มีปฏิทินวันหยุดนักขัตฤกษ์

**เทสต์:** `dotnet test` → ✅ **86/86 ผ่าน** (ของเดิม 45 + activity, comments, WIP, attachments, file storage, card counts, แล้วก็คอลัมน์ข้ามโปรเจกต์/workspace, redact คอมเมนต์, 413 ของ Kestrel, magic bytes, สมาชิก SignalR, tenant แบบ fail-closed, assignee, และ 500 ที่ไม่ส่งข้อความ exception)

### Frontend

| ส่วน | สถานะ |
|---|---|
| Login / Register | ✅ |
| Projects list + create dialog | ✅ |
| Workspace settings + invite member | ✅ |
| Shell (navbar / sidenav) | ✅ |
| **Board** — คอลัมน์, การ์ด, ลากวาง (CDK), filter (ค้นหา/ผู้รับผิดชอบ/priority) | ✅ |
| **Task detail** (offcanvas ด้านขวา) | ✅ |
| **Create task dialog** + quick add ในคอลัมน์ | ✅ |
| Optimistic update + rollback ตอนลากวางพลาด | ✅ |
| **Estimate บนการ์ด** + chip เวลาที่ใช้ + ขอบแดงเมื่อเกิน | ✅ |
| **หน้า Dashboard** สรุปรายคน + กราฟเทียบประมาณ/จริง | ✅ |
| **Realtime บอร์ด (SignalR)** — เห็นการ์ดที่คนอื่นสร้าง/ย้าย/แก้สด ๆ โดยไม่ reload | ✅ |
| **Presence** — avatar คนที่ออนไลน์อยู่บนบอร์ดเดียวกัน + badge ว่าใครกำลังแก้การ์ดใบไหน | ✅ |
| **หน้ารับคำเชิญ** (`/invitations/accept`) — accept ทันทีถ้า login แล้ว, ถ้ายังไม่ login พาไป login/register แล้วพากลับมา accept ต่ออัตโนมัติ | ✅ |
| **หน้า "งานของฉัน"** (`/my-tasks`) — รวมงานที่ตัวเองรับผิดชอบข้ามทุกโปรเจคใน workspace | ✅ |
| i18n ไทย/อังกฤษ | ✅ |

---

## 4. สิ่งที่ยังขาด

> รายการด้านล่างอัปเดตตามเท่าที่ทราบ ณ 2026-10-17 — ของจริงดูที่บอร์ด **WL** เป็นหลัก

### ✅ ฝั่ง backend ทำแล้วในรอบ 2026-10-17 (ฝั่ง Angular อยู่คนละ repo จะต่อจากสัญญา API นี้)

- Activity log, comments, attachments, WIP limit — ดูรายละเอียดในหัวข้อ 8
- WL-35 ตัดสินแล้ว: งานที่ตกวันเสาร์-อาทิตย์นับเป็น 0 วันทำการ

หน้ารายละเอียดการ์ดฝั่งเว็บ (แสดง log / คอมเมนต์ / ไฟล์ / คำเตือน WIP) ยังไม่ได้อยู่ใน repo นี้

---

## 5. งานที่ทำวันที่ 2026-08-22

### 🐛 บั๊กที่เจอและแก้แล้ว

**1. สร้างการ์ดไม่ได้เลย — API ตอบ 500** *(บั๊กจริงใน production code)*

`BoardService.cs:142` เรียก `.FirstAsync()` ต่อท้าย raw SQL ที่เป็น `INSERT ... RETURNING` ซึ่ง EF Core
พยายามครอบ `LIMIT 1` ทับ statement ที่ compose ไม่ได้ →

```
'FromSql' or 'SqlQuery' was called with non-composable SQL
and with a query composing over it.
```

**ผลกระทบ:** ฟีเจอร์หลักของ Phase 2 ทั้งหมด (สร้างการ์ด) พังสนิท ยิงเมื่อไหร่ก็ 500

**แก้เป็น:** เปลี่ยนไปใช้ `.ToListAsync()` ที่ไม่ compose ทับ และ alias คอลัมน์เป็น `AS "Value"`
ให้ตรงกับที่ `SqlQuery<T>` คาดหวัง — ยืนยันแล้วว่าสร้างการ์ดผ่าน ได้ `DEMO-1` กลับมาปกติ

**2. เทสต์บอร์ด 7 ตัวพัง — deserialize enum ไม่ได้** *(บั๊กฝั่งเทสต์)*

`TaskMoveReorderTests.cs` เรียก `ReadFromJsonAsync<T>()` เปล่า ๆ ทั้ง 11 จุด ทั้งที่ server ส่ง enum
เป็น string (`"Free"`, `"Medium"`) → `JsonException` ตั้งแต่ setup

ที่จริง `TestAuthHelper.JsonOptions` เตรียม `JsonStringEnumConverter` ไว้ให้แล้ว แค่ไฟล์เทสต์ Phase 2
ลืมหยิบไปใช้ → เติมให้ครบทุกจุด

**3. เลือก priority = Low ไม่ได้ — กลายเป็น Medium ทุกครั้ง** *(บั๊กจริงใน production code)*

เจอตอนสร้างการ์ดจริงในบอร์ด `WL` — ตั้งไว้ 3 ใบเป็น Low แต่บันทึกออกมาเป็น Medium ทั้งหมด

ต้นเหตุ: `TaskItemConfiguration.cs:16` ใส่ `.HasDefaultValue(TaskPriority.Medium)` ไว้
แต่ `TaskPriority.Low` มีค่า **0 ซึ่งเป็น CLR default** → EF Core ตีความว่า "ยังไม่ได้ตั้งค่า"
แล้ว**ตัดคอลัมน์นี้ออกจาก INSERT** ปล่อยให้ default ของ database (`'Medium'`) เขียนทับ

**ผลกระทบ:** ผู้ใช้เลือก Low จากหน้าจอไม่ได้เลย ระบบเงียบ ๆ เปลี่ยนเป็น Medium ให้ ไม่มี error

**แก้เป็น:** `.HasSentinel((TaskPriority)(-1))` — ย้าย sentinel ไปค่าที่ assign ไม่ได้
EF เลยส่ง priority จริงทุกครั้ง + เพิ่มเทสต์ `CreateTask_PersistsEveryPriority` ครอบทั้ง 4 ระดับ

**ผลรวม:** `dotnet test` จาก **11 ผ่าน / 7 พัง** → ✅ **22 ผ่าน / 0 พัง**

### 📋 บอร์ดติดตามงานตัวเอง

สร้างโปรเจค **`WL — Worklane`** ในระบบ แล้วใส่การ์ด **35 ใบ** ที่สะท้อนสถานะจริงของโค้ดชุดนี้
(Backlog 9 · To Do 2 · In Progress 0 · Done 24) ทุกใบมีประมาณการเป็นแมนเดย์

ใช้บอร์ดนี้เป็นแหล่งอ้างอิงหลักว่างานไปถึงไหน — การ์ดแต่ละใบมีรายละเอียดข้างในว่าทำอะไรไป
ติดตรงไหน และทำไมถึงต้องทำ

→ http://localhost:4200/w/demo-users-workspace/p/WL/board

### 🔧 สภาพแวดล้อมที่ตั้งไว้

- เครื่องมี **PostgreSQL 18 native จองพอร์ต 5432 อยู่แล้ว** → รัน container ที่พอร์ต **5433** แทน
  โดย mount volume เดิม `worklane_pgdata` (ข้อมูลเก่าอยู่ครบ ไม่ได้ลบ)
- API รันด้วย env `ConnectionStrings__Default` ชี้ไป 5433 → migration ถูก apply อัตโนมัติ
- โปรเจค `DEMO` เดิมปล่อยไว้ตามเดิม (การ์ดทดสอบที่สร้างตอนไล่บั๊ก archive ทิ้งแล้ว)

### 📊 Phase 3 — วัดผลเป็นแมนเดย์

ใส่ประมาณการเป็นแมนเดย์ตอนสร้างการ์ด ระบบจับเวลาจริงให้เองตอนลาก แล้วสรุปออกมาเป็นรายคน

**สิ่งที่วัดได้:** ปริมาณงาน (รอทำ / กำลังทำ / เสร็จ) · แมนเดย์ประมาณ vs จริง · ส่วนต่าง ·
จำนวนใบที่เกินประมาณ · เลยกำหนดกี่ใบกี่วัน · อัตราส่งตรงเวลา · cycle time เฉลี่ย

**ข้อจำกัดที่พิมพ์ไว้ใต้ตาราง Dashboard เลย:**
- นับ elapsed ไม่ใช่ effort — ถือ 3 ใบพร้อมกันจะนับเวลาเต็มทั้ง 3 ใบ
- ตัดแค่เสาร์-อาทิตย์ ไม่มีปฏิทินวันหยุดนักขัตฤกษ์

**ข้อมูลบนบอร์ดตอนนี้:** ทุกใบมีประมาณการ (รวม 47 วัน) แต่ตัวเลข "ใช้จริง" มีแค่ใบเดียว
คือ `WL-15` ที่ทั้งวันเริ่มและวันจบเป็นข้อเท็จจริงจาก git — งาน frontend มีอยู่ตั้งแต่ 15 ก.ค.
แต่เพิ่ง commit 22 ส.ค. → **ประมาณ 0.5 วัน ใช้จริง 27.5 วัน เกินไป 27 วัน**

ไม่ได้เดาเวลาย้อนหลังให้ใบอื่น เพราะไม่มีหลักฐานว่าแต่ละใบใช้เวลาเท่าไหร่จริง ๆ
การวัดผลจริงเริ่มนับตั้งแต่ตอนนี้เป็นต้นไป

**⚠️ เจอระหว่างทดสอบ:** 22 ส.ค. 2026 เป็น**วันเสาร์** งาน Phase 3 ทั้งหมดเลยได้เครดิต 0 วันทำการ
เป็นผลจากดีไซน์ที่เลือกไว้ ไม่ใช่บั๊ก — แต่ต้องตัดสินใจว่าจะเอาแบบนี้ไหม (การ์ด `WL-35`)

---

## 6. วิธีรัน (สภาพเครื่องปัจจุบัน)

```bash
docker start worklane-pg
```

```bash
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__Default="Host=localhost;Port=5433;Database=worklane;Username=worklane;Password=worklane" \
dotnet run --project src/Worklane.WebApi --no-launch-profile --urls http://localhost:5000
```

```bash
cd web && npm start
```

| ปลายทาง | URL |
|---|---|
| เว็บ | http://localhost:4200 |
| Swagger | http://localhost:5000/swagger |
| **บอร์ดติดตามงาน Worklane** | http://localhost:4200/w/demo-users-workspace/p/WL/board |

**Login:** `demo@worklane.app` / `Demo123!`

> 💡 ถ้าจะรัน `dotnet test` ต้อง **ปิด API ที่รันค้างอยู่ก่อน** ไม่งั้น build fail เพราะไฟล์ `Worklane.Infrastructure.dll` ถูกล็อก

**อีเมลเชิญจริง (ไม่บังคับ):** ไม่ตั้งค่าไว้ก็ยังใช้งานได้ปกติ — แค่ log warning แล้วลิงก์เชิญยัง log ไว้ใน console เหมือนเดิม ถ้าจะให้ส่งอีเมลจริงผ่าน Gmail:

```bash
dotnet user-secrets set "Email:SmtpUsername" "you@gmail.com" --project src/Worklane.WebApi
dotnet user-secrets set "Email:SmtpPassword" "<App Password จาก https://myaccount.google.com/apppasswords>" --project src/Worklane.WebApi
```

ต้องเปิด 2-Step Verification ในบัญชี Google ก่อนถึงจะสร้าง App Password ได้ — credential เก็บนอก repo เสมอ (user-secrets), ห้ามใส่ใน `appsettings.json`

---

## 7. ลำดับงานที่ควรทำต่อ

> รายการที่เคยอยู่ตรงนี้ (commit ค้าง, UI จัดการคอลัมน์, DevSeeder, SignalR, Dashboard) ทำเสร็จหมดแล้ว
> ลำดับงานจริงตอนนี้อยู่ที่บอร์ด **WL** (Backlog → To Do → In Progress → Done) ไม่ใช่ตารางนี้ — ดูที่บอร์ดแทน

---

## 8. รอบ 2026-10-17 — activity, comments, WIP, attachments

ทำฝั่ง API เพื่อให้ frontend (repo `Worklane-Web`) สร้างหน้ารายละเอียดการ์ดต่อได้ สัญญา JSON อยู่ใน `README.md`

**Activity** — `GET /api/tasks/{taskId}/activity` เรียงใหม่สุดก่อน แถวเดิมของการสร้าง/ย้ายคอลัมน์ถูก backfill เป็น `Created` / `Moved` พร้อมชื่อคอลัมน์ใน `details` การแก้การ์ดหนึ่งครั้งที่แตะหลายฟิลด์จะเขียนแถว `Updated` **หนึ่งแถวต่อหนึ่งฟิลด์** (`{ field, oldValue, newValue }`) การลากภายในคอลัมน์เดิมไม่บันทึก (พฤติกรรมเดิม) การส่ง `columnId` ผ่าน PATCH นับเป็น `Updated` ไม่ใช่ `Moved` — นาฬิกาแมนเดย์ขยับเฉพาะ `POST /move`

**Comments** — เรียงเก่าสุดก่อน `updatedAt` เป็น `null` จนกว่าผู้เขียนจะแก้ แก้ได้เฉพาะผู้เขียน ลบได้ทั้งผู้เขียนและ admin/owner ของ workspace SignalR: `CommentCreated` / `CommentUpdated` (ฟิลด์ของ comment + `projectId`) และ `CommentDeleted` (`{ projectId, taskId, commentId }`)

**WIP** — `wipLimit` เป็น null ได้ ตั้งตอนสร้างคอลัมน์หรือ PATCH (`null` คือล้าง, ไม่ส่งฟิลด์มาคือไม่แตะ) เกินลิมิตแล้วยังสร้าง/ย้ายได้ `isOverWipLimit` เป็นจริงเมื่อจำนวนการ์ด **มากกว่า** ลิมิต (เท่ากับลิมิตยังไม่เกิน) ฟิลด์นี้อยู่บน response ของการ์ดทุกเส้นเพราะใช้ DTO เดียวกัน และอยู่บนคอลัมน์คู่กับ `taskCount`

**Attachments** — multipart ฟิลด์ `file` เก็บผ่าน `IFileStorage` + `LocalFileStorage` ตาม `FileStorage:LocalPath` (ค่าเริ่มต้น 10 MB) ชนิดที่ไม่อยู่ใน allow-list ได้ 415, เกินขนาดได้ 413, ไฟล์ว่างได้ 400 ดาวน์โหลดตั้ง `Content-Disposition: attachment` และเช็ค workspace จาก id ของไฟล์ ไม่มี static path ให้เดาข้าม tenant ลบได้เฉพาะผู้อัปโหลดหรือ admin/owner `docker-compose` mount volume `attachments` SignalR: `AttachmentAdded` / `AttachmentDeleted`

**WL-35** — คงกฎวันทำการจันทร์–ศุกร์ งานทั้งก้อนที่เกิดในวันเสาร์-อาทิตย์ได้ 0 วันทำการ ไม่ได้เปลี่ยนสูตร `WorkingDays`
