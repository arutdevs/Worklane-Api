# Worklane — Development Log

> บันทึกสถานะโปรเจค ว่าตอนนี้ไปถึงไหน ทำอะไรไปแล้วบ้าง และอะไรยังขาด
> อัปเดตล่าสุด: **2026-08-22**

---

## 1. ภาพรวม

Worklane = ระบบจัดการงานแบบ multi-tenant (Jira ฉบับย่อ) แยกเป็น **2 git repository**:

| Repo | Path | Branch | Commit ล่าสุด |
|---|---|---|---|
| Backend | `C:\dev\worklane` | `develop` | `de6aee0` (2026-08-22) |
| Frontend | `C:\dev\worklane\web` | — | `7dcac66` (2026-07-15) |

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
| 2026-08-22 | กลับมารันโปรเจค, audit สถานะ, **เจอและแก้บั๊ก 3 จุด** → commit `de065da`, สร้างบอร์ด `WL` ติดตามงานตัวเอง, เพิ่ม log นี้ → commit `de6aee0` |

---

## 3. สิ่งที่ทำเสร็จแล้ว

### Backend — เสร็จและมีเทสต์คุม

**Domain (9 entities):** `Workspace` `WorkspaceMember` `Invitation` `Project` `ProjectMember` `BoardColumn` `TaskItem` `ProjectTaskCounter` `RefreshToken`

**API endpoints ทั้งหมด 25 เส้น:**

| กลุ่ม | Endpoints |
|---|---|
| Auth | `POST /api/auth/register` `login` `refresh` `logout` |
| Workspaces | `GET/POST /api/workspaces` · `GET {id}/members` · `POST {id}/invitations` · `PATCH/DELETE {id}/members/{userId}` |
| Invitations | `POST /api/invitations/accept` |
| Projects | `GET/POST /api/workspaces/{id}/projects` · `PATCH/DELETE /api/projects/{id}` |
| Columns | `GET/POST /api/projects/{projectId}/columns` · `PATCH/DELETE /api/columns/{columnId}` |
| Tasks | `GET/POST /api/projects/{projectId}/tasks` · `GET/PATCH/DELETE /api/tasks/{taskId}` · `POST /api/tasks/{taskId}/move` |

**จุดที่ออกแบบไว้ดี:**
- **Multi-tenancy 2 ชั้น** — `[RequireWorkspaceRole]` อ่าน `WorkspaceId` จาก *route* เท่านั้น (ไม่เชื่อ body) + EF global query filter กันข้อมูลข้าม tenant ซ้ำอีกชั้น
- **Fractional ordering** — การ์ดใช้ `Order` เป็น `double` แล้วหาค่ากึ่งกลางตอนลาก ไม่ต้อง UPDATE ทั้งคอลัมน์
- **Task number แบบ atomic** — `INSERT ... ON CONFLICT DO UPDATE RETURNING` กันเลขซ้ำตอนสร้างพร้อมกัน (`DEMO-1`, `DEMO-2`, ...)

**เทสต์:** `dotnet test` → ✅ **22/22 ผ่าน** (auth flow, workspace authorization, task move/reorder, priority)

### Frontend — โค้ดครบ แต่ยังไม่ commit

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
| i18n ไทย/อังกฤษ | ✅ (board 13 key, task 18 key) |

---

## 4. สิ่งที่ยังขาด

### 🔴 ติดขัดจนใช้งานไม่ได้

- **ไม่มีปุ่ม "เพิ่มคอลัมน์" ใน UI** — `BoardService.createColumn()` / `updateColumn()` / `deleteColumn()` เขียนพร้อมแล้ว แต่ `board.html` มีแค่ empty state `board.noColumns` ไม่มีปุ่มเรียก → **โปรเจคใหม่ที่ยังไม่มีคอลัมน์จะตัน สร้างอะไรต่อไม่ได้เลย**
- **`DevSeeder` ไม่ยอม seed บอร์ด** — `DevSeeder.cs:18` เช็ค `if (demo user มีอยู่แล้ว) return;` ตั้งแต่บรรทัดแรก ทำให้โค้ด seed คอลัมน์/การ์ด (บรรทัด 69–128) ไม่เคยรันในเครื่องที่เคยรัน Phase 1 มาก่อน

### 🟡 ยังไม่ได้เริ่ม (ตรงกับเป้าหมายที่ตั้งไว้)

- **บอกสถานะคนอื่นแบบ realtime** — `NotificationsHub` เป็นคลาสเปล่า ไม่มี event เลย และฝั่ง Angular **ไม่มีโค้ด SignalR สักบรรทัด** (มี `@microsoft/signalr` ใน package.json เฉย ๆ)
- **หน้าสรุปผลรายคน / Dashboard** — ยังไม่มี route, ไม่มี endpoint, sidenav มีแค่ Projects กับ Workspace settings
- **"งานของฉัน" (My tasks)** — ยังไม่มี
- Comment / attachment / activity log ในการ์ด
- แก้ชื่อ–ลบคอลัมน์จาก UI, WIP limit
- ส่งอีเมลเชิญจริง (ตอนนี้ log ลิงก์ออก console)
- **CI** — `.github/` ว่างเปล่า ไม่มี workflow เลย ทุกอย่างพึ่งการรันมือล้วน ๆ
  (ถ้ามี CI ตั้งแต่แรก บั๊ก 500 ตอนสร้างการ์ดคงไม่หลุดมาได้ 5 สัปดาห์)

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

สร้างโปรเจค **`WL — Worklane`** ในระบบ แล้วใส่การ์ด **29 ใบ** ที่สะท้อนสถานะจริงของโค้ดชุดนี้
(4 คอลัมน์: Backlog 8 · To Do 3 · In Progress 1 · Done 17)

ใช้บอร์ดนี้เป็นแหล่งอ้างอิงหลักว่างานไปถึงไหน — การ์ดแต่ละใบมีรายละเอียดข้างในว่าทำอะไรไป
ติดตรงไหน และทำไมถึงต้องทำ

→ http://localhost:4200/w/demo-users-workspace/p/WL/board

### 🔧 สภาพแวดล้อมที่ตั้งไว้

- เครื่องมี **PostgreSQL 18 native จองพอร์ต 5432 อยู่แล้ว** → รัน container ที่พอร์ต **5433** แทน
  โดย mount volume เดิม `worklane_pgdata` (ข้อมูลเก่าอยู่ครบ ไม่ได้ลบ)
- API รันด้วย env `ConnectionStrings__Default` ชี้ไป 5433 → migration `AddBoardEntities` ถูก apply อัตโนมัติ
- โปรเจค `DEMO` เดิมปล่อยไว้ตามเดิม (การ์ดทดสอบที่สร้างตอนไล่บั๊ก archive ทิ้งแล้ว)

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

---

## 7. ลำดับงานที่ควรทำต่อ

| # | งาน | ทำไม |
|---|---|---|
| 1 | **commit งาน frontend ที่ค้าง 31 ไฟล์** | งาน Phase 2 + redesign ทั้งหมดยังลอยอยู่ในเครื่องเดียว ไม่มี backup |
| 2 | เพิ่ม UI จัดการคอลัมน์ (เพิ่ม/แก้ชื่อ/ลบ) | ปลดล็อกโปรเจคใหม่ที่ตอนนี้ตัน |
| 3 | แก้ `DevSeeder` ให้ seed บอร์ดแยกจากการเช็ค user | ให้เครื่อง dev ใหม่มีข้อมูลตัวอย่างเสมอ |
| 4 | **SignalR events** — broadcast ตอน create/move/update การ์ด | เป้าหมาย "เห็นสถานะคนอื่น" |
| 5 | **Dashboard สรุปผลรายคน** — endpoint + หน้าจอ | เป้าหมาย "สรุปผลของแต่ละคน" |
| 6 | หน้า "งานของฉัน" ข้ามโปรเจค | ใช้งานจริงประจำวัน |
