# รัน Worklane ทั้งสแตกบนเครื่อง

คำสั่งเดียวจาก repo `Worklane-Api` สร้าง image ของ API, สร้าง image จริงของ Angular (`Worklane-Web`, nginx-unprivileged พอร์ต 8080), เปิด Postgres 16, รัน EF migration ผ่านเซอร์วิส `migrator` ครั้งเดียว แล้วเสิร์ฟแอปที่ localhost ให้ล็อกอินผ่านเว็บไปที่ API ได้

## สิ่งที่ต้องมี

- Docker Engine พร้อม Docker Compose **v2.24 ขึ้นไป** (`docker compose version`) ใช้แท็ก `!override` / `!reset` ใน `compose.local.yml`
- Git
- โคลนสอง repo ไว้ข้างกัน แล้วใช้สาขา `develop` ทั้งคู่

```text
Worklane-Api/
Worklane-Web/
```

ถ้าเว็บไม่ได้อยู่ข้างกัน ตั้ง `WORKLANE_WEB_DIR` เป็นพาธสัมบูรณ์ของ `Worklane-Web` ใน `.env` หรือในเชลล์ก่อนรัน

บน Windows ใช้ Git Bash หรือ WSL เพราะจุดเข้าคือสคริปต์ bash

## คำสั่ง

จากโฟลเดอร์ `Worklane-Api`:

```bash
./run-local.sh
```

สคริปต์จะสร้าง `.env` จาก `.env.example` ถ้ายังไม่มี แล้วสุ่ม `JWT_SIGNING_KEY` เฉพาะเครื่องนี้ ค่านี้ไม่ถูก commit (`.env` อยู่ใน `.gitignore`) ไม่มีรหัสจริงใน git

ถ้า `Worklane-Web/docker-compose.yml` มีอยู่ สคริปต์แทรกไฟล์นั้นไว้ระหว่างไฟล์ของ API กับ `compose.local.yml` (override ของเว็บที่ปัก `web` ที่ `172.30.20.3`)

คำสั่งข้างล่างคือสิ่งที่สคริปต์เรียก หลังมี `.env` และตั้ง `WORKLANE_WEB_DIR` แล้ว

```bash
docker compose -f docker-compose.yml -f compose.local.yml up --build --detach --wait
```

## เปิดแอปและล็อกอิน

เปิด http://127.0.0.1:4200

บัญชีตัวอย่างจาก seed ของ Development:

| | |
|---|---|
| อีเมล | `demo@worklane.app` |
| รหัสผ่าน | `Demo123!` |

ล็อกอินของหน้าเว็บยิงไปที่ API ผ่าน nginx ของ image เว็บ (`/api` บนพอร์ตเดียวกับหน้าเว็บ) พอร์ต 4200 คือค่า `WORKLANE_WEB_PORT`

## หยุดและล้างข้อมูล

หยุดคอนเทนเนอร์ แต่เก็บ volume ของฐานข้อมูล:

```bash
./run-local.sh down
```

ลบคอนเทนเนอร์และ volume (ฐานข้อมูลและไฟล์แนบหาย รันครั้งถัดไปจะ migrate และ seed ใหม่):

```bash
./run-local.sh down -v
```

## พอร์ตชนกัน

ค่าเริ่มต้นผูกเฉพาะ `127.0.0.1`

| เซอร์วิส | ตัวแปรใน `.env` | ค่าเริ่มต้น | หมายเหตุ |
|---|---|---|---|
| Postgres | `WORKLANE_PG_PORT` | `5434` | ไม่ใช้ `5432` (Postgres ที่ติดตั้งในเครื่องมักจองไว้) และไม่ใช้ `5433` (คอนเทนเนอร์ `worklane-pg` เดิม) |
| เว็บ Angular | `WORKLANE_WEB_PORT` | `4200` | ในคอนเทนเนอร์ยังฟัง 8080 |
| API ตรง | `WORKLANE_API_PORT` | `5000` | Swagger ใน Development อยู่ที่พอร์ตนี้ |
| nginx ขอบ API | `WORKLANE_EDGE_PORT` | `8080` | พร็อกซีที่ API เชื่อถือ ไม่ใช่หน้าเว็บ |

ถ้าขึ้นว่าพอร์ตถูกใช้แล้ว เปลี่ยนตัวแปรใน `.env` แล้วรัน `./run-local.sh down` และ `./run-local.sh` ใหม่ ตัวอย่างเมื่อ `5434` ไม่ว่าง:

```bash
# ใน .env
WORKLANE_PG_PORT=5435
```

ดูว่าใครจองพอร์ตอยู่:

```bash
docker ps --format '{{.Names}}\t{{.Ports}}'
```

API ในเน็ตเวิร์กของ Compose ต่อ Postgres ที่ `postgres:5432` เสมอ การเปลี่ยนพอร์ตบนโฮสต์ไม่ต้องแก้ connection string
