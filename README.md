<span id="readme-top"></span>
<div align="center">

<h1>Tranga-Plus</h1>

<p><b>Automatic manga downloader for your home server.</b><br/>
Pick a series once, and Tranga-Plus downloads every chapter, keeps checking for new ones,<br/>
and saves them as <code>.cbz</code> files that Kavita, Komga or any comic reader can open.</p>

![GitHub License](https://img.shields.io/github/license/croissantpigbear/tranga)
![Platforms](https://img.shields.io/badge/runs%20on-PC%20%7C%20Mac%20%7C%20Raspberry%20Pi%20%7C%20Unraid-blue)

<p>
Tranga-Plus is a fork of <a href="https://github.com/C9Glax/tranga">C9Glax/tranga</a> with many extra features (see <a href="#features">Features</a>).<br/>
This repository is the <b>API</b> (the part that does the work). The website you use it with is
<a href="https://github.com/croissantpigbear/tranga-website">croissantpigbear/tranga-website</a>, and both are installed together.
</p>

</div>

---

## Contents

1. [Features](#features)
2. [What you need](#what-you-need)
3. [Install on a PC, Mac or Raspberry Pi](#install-on-a-pc-mac-or-raspberry-pi)
4. [Install on Unraid](#install-on-unraid)
5. [First steps in the website](#first-steps-in-the-website)
6. [Updating](#updating)
7. [Backups and restoring](#backups-and-restoring)
8. [Login and using it away from home](#login-and-using-it-away-from-home)
9. [Troubleshooting](#troubleshooting)
10. [Reference: folders and settings](#reference-folders-and-settings)
11. [For developers](#for-developers)
12. [Credits and licence](#credits-and-licence)

---

## Features

Everything the original Tranga does (download from MangaDex, `.cbz` files, Komga/Kavita library scans, notifications to
Gotify/Ntfy/Pushover/webhooks), plus:

**More websites**
- **Hundreds of websites through [FMD2](https://github.com/dazedcat19/FMD2) modules.** Upload a site's `.lua` file and it
  works like a built-in site: search, add by link, chapters, downloads. Tested with MangaFire, Asura Scans and Weeb Central.
- Test a site on any manga link, switch sites on or off, build a site's title list for faster searching.
- **Module update checker:** tells you when FMD2 has a newer version of a module you uploaded, and updates it in one click (with undo).
- Website icons next to every series, so you can see where it downloads from.
- MangaDex stays built in. Series on the old built-in AsuraComic, Mangaworld and Weeb Central connectors move to the matching uploaded module by themselves.

**Downloading**
- **Download queue:** series download one after another in the order you added them, not all mixed up.
  Reorder the queue, pause one series or pause everything (Activity → Download queue).
- **Failed downloads are handled:** Tranga retries after 5 min, 30 min, 2 h and 12 h, then gives up. If the series is also
  on another website, it switches to that one, and it can notify you. Failed chapters are listed at the top of Activity → Download queue, with a Retry button.
- **Page-level retries:** a broken page is retried on its own, and pages that worked are kept for the next try.
- **Start from chapter:** skip the chapters you've already read elsewhere (when adding a series, or later on its page).
- **Preferred website:** a series on two websites downloads each chapter once, from the starred website first, with the other filling gaps.
- **Re-download** any chapter (from the same or another website) when pages are missing or low quality.
- Limits for downloads at once, in total and per website. Presets for a Raspberry Pi, NAS or desktop (Settings → Performance).
- **Schedules:** choose how often Tranga checks for new chapters, refreshes details, sends notifications and so on, with recommended values (Settings → Schedules).

**Your library**
- **Import existing folders:** add series you already have on disk. Tranga recognises the chapters that are already there and only downloads the missing ones.
- **Same name, different series:** "Title" and "Title [R18]", coloured editions, or two different series with exactly the same name are kept apart, each in its own folder.
- **Release calendar:** new chapters by day, and when each series' next chapter is likely.
- Delete single chapter files, see file sizes and disk use, rename series folders with a naming scheme.

**Details and reading apps**
- Series details from **AniList**, **MangaUpdates** and **MangaDex**: alternative titles, writers and artists, publisher, genres, rating, status, content rating.
- Full **ComicInfo.xml** in every chapter, so Kavita and Komga show proper details. Existing chapters can be updated in one go.
- **Kavita covers:** Tranga's covers are sent to Kavita automatically.

**Peace of mind**
- **Optional login**, off by default, switched on in Settings → Login. Includes API keys for other apps.
- **Daily backups** of the database and settings, with their status shown in Settings → Maintenance.
- **Settings backup and restore:** one `.zip` to move Tranga's settings and modules to another server.
- **Activity page:** what Tranga did, what's running, live logs (downloadable), the queue and failed downloads.
- Works on phones, has light and dark modes, and applies settings straight away without a restart.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## What you need

- A computer that stays on: a PC or Mac, a **Raspberry Pi 4 or 5** (64-bit Raspberry Pi OS), or an **Unraid** server.
- **Docker.** The steps below show how to install it.
- About 1 GB of memory for Tranga-Plus, plus disk space for your manga.

Tranga-Plus is three small programs (called *containers*) that Docker runs together:

| Container | What it does |
|---|---|
| `tranga-website` | The website you open in your browser (port **9555**) |
| `tranga-api` | Does the work: searching, downloading, checking for new chapters |
| `tranga-pg` | The database (PostgreSQL) |
| `tranga-backup` | Makes a backup of the database and settings every night |

You don't have to build anything: ready-made images are downloaded from GitHub (`ghcr.io/croissantpigbear/...`).

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Install on a PC, Mac or Raspberry Pi

Type each command into a terminal (on a Raspberry Pi or Linux: the Terminal app or SSH; on a Mac: Terminal).
Lines starting with `#` are explanations, so don't type them.

### Step 1: Install Docker

- **Raspberry Pi / Linux:**
  ```bash
  curl -fsSL https://get.docker.com | sh
  sudo usermod -aG docker $USER
  ```
  Then **log out and back in** (or reboot) so you can use Docker without `sudo`.
  On a Raspberry Pi, check that you have the 64-bit system: `uname -m` must say `aarch64`.
- **Mac or Windows:** install [Docker Desktop](https://www.docker.com/products/docker-desktop/) and start it.

Check Docker works: `docker compose version` should print a version number.

### Step 2: Make a folder and download the two setup files

```bash
mkdir -p ~/tranga-plus && cd ~/tranga-plus
curl -fsSLo docker-compose.yaml https://raw.githubusercontent.com/croissantpigbear/tranga/tranga-plus/docker-compose.yaml
curl -fsSLo .env https://raw.githubusercontent.com/croissantpigbear/tranga/tranga-plus/.env.example
mkdir -p Manga settings postgres backups
```

> **Why `mkdir`?** If Docker creates these folders itself, they belong to the administrator account (root) and Tranga can't
> write to them. Making them yourself avoids "Permission denied" errors.

### Step 3: Set your password and time zone

Open the `.env` file in a text editor (`nano .env`; save with Ctrl+O, Enter, then exit with Ctrl+X) and change:

| Line | Change it to |
|---|---|
| `POSTGRES_PASSWORD=change-me-to-something-long` | Any long password. You won't need to type it anywhere else. |
| `TZ=Europe/London` | Your time zone ([list](https://en.wikipedia.org/wiki/List_of_tz_database_time_zones), "TZ identifier" column) |
| `UID=1000` / `GID=1000` | The output of `id -u` and `id -g`. It's usually 1000 on a Pi or Linux. On a Mac, use the numbers it shows (often 501 and 20). |

### Step 4: Start it

```bash
docker compose pull
docker compose up -d
```

The first start takes a minute or two while the database is set up. Check everything is running:

```bash
docker compose ps
```

All four containers should say `running` (the database also shows `healthy`).

### Step 5: Open the website

Go to **`http://<this computer's IP address>:9555`** in your browser (e.g. `http://192.168.1.50:9555`).
On the same computer, `http://localhost:9555` works too. To find the IP address, run `hostname -I` on a Pi or Linux;
on a Mac, look in System Settings → Network.

Now continue with [First steps in the website](#first-steps-in-the-website).

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Install on Unraid

You'll use the **Docker Compose Manager** plugin. It takes about 10 minutes.

### Step 1: Install the plugin

**Apps** tab → search **Docker Compose Manager** → **Install**.

### Step 2: Make the folders (with the right owner)

Open the Unraid **terminal** (the `>_` icon at the top right) and paste:

```bash
mkdir -p /mnt/user/appdata/tranga/settings /mnt/user/appdata/tranga/backups /mnt/cache/appdata/tranga/postgres /mnt/user/Manga
chown -R 99:100 /mnt/user/appdata/tranga/settings /mnt/user/appdata/tranga/backups /mnt/user/Manga
```

> **No cache pool, or your pool has another name?** Replace `/mnt/cache/` with your pool's path (e.g. `/mnt/nvme/`), or with
> `/mnt/disk1/` if you have no pool, here **and** in the compose file in step 4. Don't put the database on `/mnt/user/`
> because it's slow and can corrupt the database.
>
> **Manga somewhere else?** Use your own share instead of `/mnt/user/Manga`, here and in step 4.

### Step 3: Create the stack

**Docker** tab → scroll to the bottom → **Compose** section → **Add New Stack** → name it `tranga-plus` → **OK**.

### Step 4: Paste the compose file

Click the gear icon next to `tranga-plus` → **Edit Stack** → **Compose File**.
Delete what's there, then paste the whole of
[`docker-compose.unraid.yml`](https://raw.githubusercontent.com/croissantpigbear/tranga/tranga-plus/docker-compose.unraid.yml)
→ **Save Changes**.

### Step 5: Paste the password file

Gear icon → **Edit Stack** → **Env File**. Paste this, with your own password, then **Save Changes**:

```
POSTGRES_PASSWORD=pick-a-long-password-here
TZ=Europe/London
BACKUP_HOUR=3
BACKUP_KEEP_DAYS=7
```

### Step 6: Start it

Click **Compose Up**. When it says it's done, open **`http://<your-unraid-ip>:9555`**.

Continue with [First steps in the website](#first-steps-in-the-website). When you add a download folder there, use
**`/Manga`**, not `/mnt/user/Manga`.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## First steps in the website

### 1. Tell Tranga where to save manga

**Settings → Libraries → Add download folder**

- **Library Name:** anything, e.g. `Manga`
- **Directory Path:** **`/Manga`**

> ⚠️ Always use **`/Manga`** here, the folder's path *inside* the container. Docker connects it to the real folder
> (`~/tranga-plus/Manga`, or `/mnt/user/Manga` on Unraid). Typing the real path here is the most common setup mistake.

### 2. (Optional) Add more websites

MangaDex works straight away. For other websites:

1. Go to **Settings → More websites → Manage websites**. That page has step-by-step instructions.
2. Download the site's module from FMD2's [`lua/modules`](https://github.com/dazedcat19/FMD2/tree/master/lua/modules) folder
   (for example `MangaFire.lua`), using the **Download raw file** button.
3. Upload it. If the page says the module needs a template or helper (from `lua/websitebases` or `lua/utils`), upload that too.
4. Click **Test** and paste a link to a manga on that site to check it works.

### 3. Add a manga

Click **Add manga** (top right), search for a title or paste a link to the manga's page, and click **Add**.
Pick the library, then click **Add & download**. Chapters start downloading within a minute.

> **Tip:** titles often match better in their original form, e.g. *Shingeki no Kyojin* instead of *Attack on Titan*.

### 4. (Optional) Connect Kavita or Komga

**Settings → Libraries → Connect Kavita/Komga.** Enter its address (e.g. `http://192.168.1.50:5000`) and its API key (in
Kavita: your user menu → Settings → Account → API Key). Kavita's library must point to the **same real folder** Tranga
saves to (`~/tranga-plus/Manga` or `/mnt/user/Manga`).

### 5. (Optional) Already have manga on disk?

**Settings → Libraries → Import existing.** Tranga lists the folders it doesn't know. Click **Find series** for each one,
then add the matching result. The chapters already in the folder count as downloaded; only missing ones are fetched.

### Where to find things

| I want to… | Go to |
|---|---|
| See what Tranga did, what's running, logs | **Activity** (top right) |
| Pause all downloads, reorder series, see failed chapters | **Activity → Download queue** |
| See new chapters by day, and what's coming | **Calendar** (top right) |
| Skip early chapters of a series | The series page → **Download** card → **Start from chapter** |
| Choose which website a series prefers | The series page → **Download** card → ☆ next to a website |
| Get a chapter again (broken pages) | The series page → chapter list → **Re-download** |
| Change how often Tranga checks for new chapters | **Settings → Schedules** |
| Make downloads faster or lighter | **Settings → Performance** |
| Change file and folder names | **Settings → Naming** |
| Get notifications on my phone | **Settings → Notifications** |
| Protect it with a password | **Settings → Login** |
| Back up or move my settings | **Settings → Maintenance** |

On a phone, Activity, Calendar and the light/dark switch are in the ☰ menu.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Updating

**PC, Mac, Raspberry Pi:**

```bash
cd ~/tranga-plus
docker compose pull
docker compose up -d
```

**Unraid:** Docker tab → `tranga-plus` → **Update Stack** (or **Compose Pull**, then **Compose Up**).

Your manga, settings and database are kept. Updates also bring new database changes, which are applied automatically when
the API starts.

> When a new version changes the compose file (a new container or folder, for example), the release notes will say so.
> Download the new `docker-compose.yaml` (or `docker-compose.unraid.yml`) and replace yours, keeping your `.env`.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Backups and restoring

### Automatic daily backups

The `tranga-backup` container saves, every night at `BACKUP_HOUR` (default 3am), into the `backups` folder:

- `tranga-db-<date>.dump`: the whole database (your series, chapters, libraries, connections)
- `tranga-settings-<date>.tar.gz`: the settings folder (settings, FMD2 modules, login, schedules)

It keeps `BACKUP_KEEP_DAYS` days of them (default 7). **Settings → Maintenance** shows when the last one was made:
**OK**, **Out of date** or **None yet**.

> **Copy the `backups` folder somewhere else now and then** (another disk, a USB stick, cloud storage). A backup on the same
> disk won't help if that disk dies.

### Settings backup (one click)

**Settings → Maintenance → Download settings** saves a `.zip` with your settings and FMD2 modules.
**Restore from a file…** puts them back. Tranga shows what's in the file first, keeps a copy of the current settings, then
restarts by itself. This is handy for moving to a new server. Your series are in the database, so restore those from the
daily backup (below).

### Restore the database from a backup

Pick the backup you want from the `backups` folder, then (replace the date with yours):

**PC, Mac, Raspberry Pi** (in `~/tranga-plus`):

```bash
docker compose stop tranga-api tranga-website
docker exec -i tranga-pg pg_restore -U postgres -d postgres --clean --if-exists < backups/tranga-db-2026-10-17_0300.dump
# Optional, settings too:
tar -xzf backups/tranga-settings-2026-10-17_0300.tar.gz -C settings
docker compose start tranga-api tranga-website
```

**Unraid** (terminal):

```bash
docker stop tranga-api tranga-website
docker exec -i tranga-pg pg_restore -U postgres -d postgres --clean --if-exists < /mnt/user/appdata/tranga/backups/tranga-db-2026-10-17_0300.dump
# Optional, settings too:
tar -xzf /mnt/user/appdata/tranga/backups/tranga-settings-2026-10-17_0300.tar.gz -C /mnt/user/appdata/tranga/settings
chown -R 99:100 /mnt/user/appdata/tranga/settings
docker start tranga-api tranga-website
```

A few "does not exist" messages during `pg_restore` are normal.

### Change the database password

The database password is only set when the database is first created, so changing `.env` alone isn't enough.
**If you installed with the default password `postgres`**, change it like this:

1. Change it inside the database:
   ```bash
   docker exec -it tranga-pg psql -U postgres -c "ALTER USER postgres PASSWORD 'your-new-password';"
   ```
2. Put the **same** password in `.env` (Unraid: Edit Stack → Env File) as `POSTGRES_PASSWORD=your-new-password`.
3. Apply it: `docker compose up -d` (Unraid: **Compose Up**).

Do step 1 first. If you change `.env` first, Tranga can't connect to the database. To recover, put the old password back in `.env`, then start again from step 1.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Login and using it away from home

### Switch the login on

**Settings → Login.** Choose a user name and a password (at least 8 characters), then click **Switch login on**.
Every browser now has to log in ("Stay logged in for 30 days" is ticked by default). You can switch it off again on the same page.

- **Other apps** (scripts, dashboards): create an **API key** on the same page. Send it as `Authorization: Bearer <key>`
  or `X-Api-Key: <key>`.
- **Forgot your password?**
  1. Add `- TRANGA_DISABLE_LOGIN=true` to the `tranga-api` environment (it's already in the compose file as a comment; remove the `#`).
  2. Restart (`docker compose up -d`, or **Compose Up** on Unraid).
  3. Set a new password in Settings → Login.
  4. Put the `#` back and restart again.

  (Deleting `settings/auth.json` also switches the login off.)
- After 5 wrong passwords, Tranga makes you wait a little longer before each new try.

### Using Tranga-Plus away from home

The safest way is a VPN to your home network, such as [Tailscale](https://tailscale.com/) (free, works on phones), or
WireGuard on your router. Then use the same `http://<ip>:9555` address as at home.

If you put Tranga-Plus on the internet through a reverse proxy (Nginx Proxy Manager, SWAG, Caddy, Cloudflare Tunnel),
**switch the login on first**, and use HTTPS.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Troubleshooting

**First step for any problem:** look at **Activity → Logs** in the website, or run `docker logs tranga-api --tail 100`.
Activity → Logs → **Download** saves the log to a file you can share when asking for help.

| Problem | What to do |
|---|---|
| The website doesn't open | Run `docker compose ps`: are all containers `running`? Use the computer's IP and port **9555**. Give it 2 minutes after the first start. |
| `tranga-pg` won't start (Unraid: "dependency tranga-pg failed to start") | The database folder must be on a real disk or pool, not the USB flash drive or `/mnt/user/`. Use absolute paths as in `docker-compose.unraid.yml`. |
| "Permission denied" / "Access to the path … is denied" in the log | The folders belong to another user. **PC/Pi:** `sudo chown -R $(id -u):$(id -g) Manga settings backups`. **Unraid:** `chown -R 99:100 /mnt/user/appdata/tranga/settings /mnt/user/Manga`. Then restart `tranga-api`. |
| `docker compose pull` says "denied" or "unauthorized" | The images must be public: on GitHub, open each package (`tranga-api`, `tranga-website`) → Package settings → Change visibility → Public. |
| Chapters don't download | Is the series set to download from a website (series page → Download card)? Is a library chosen? Check Activity → Download queue: downloads may be paused, or the chapter may be listed as failed with the reason. |
| "No pages found" for some chapters | The website may have removed the chapter or blocked Tranga. Open the chapter in your browser to check. Add the series from a second website, and Tranga will use it after giving up on the first. Or use **Re-download** and pick another website. |
| A website returns 403 / Cloudflare errors | Add FlareSolverr: uncomment the `flaresolverr` service and `FLARESOLVERR_URL` in the compose file, then `docker compose up -d`. |
| Search finds nothing on an FMD2 site | Use **Test** on the More websites page with a manga link. Some sites need their **title list** built first (button on the same page). Or paste the manga's link in the search box instead. |
| Kavita doesn't show new chapters or covers | Kavita's library must point to the same real folder. Check the connection in Settings → Libraries (**Test**). |
| Settings → Maintenance says "No backup folder is connected" | You're using an old compose file. Get the new one (it has the `tranga-backup` service and the `backups` folder). |
| "Max worker concurrency reached" in the log | Normal: Tranga is busy and jobs wait their turn. Raise the limit in Settings → Performance if your computer can handle it. |
| Locked out of the login | See [Forgot your password?](#switch-the-login-on) |

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Reference: folders and settings

### Folders

| Folder (PC/Pi) | Unraid | Inside the container | What's in it |
|---|---|---|---|
| `Manga/` | `/mnt/user/Manga` | `/Manga` | Downloaded chapters, one folder per series |
| `settings/` | `/mnt/user/appdata/tranga/settings` | `/usr/share/tranga-api` | Settings, FMD2 modules (`lua/`), login (`auth.json`), queue, schedules |
| `postgres/` | `/mnt/cache/appdata/tranga/postgres` | `/var/lib/postgresql/data` | The database. Don't touch it. |
| `backups/` | `/mnt/user/appdata/tranga/backups` | `/backups` | Daily backups |

### `.env` file

| Setting | Default | Meaning |
|---|---|---|
| `POSTGRES_PASSWORD` | `postgres` | Database password (see [Change the database password](#change-the-database-password)) |
| `TZ` | `Europe/London` | Time zone for log times and the backup hour |
| `BACKUP_HOUR` | `3` | Hour of the day (0–23) for the daily backup |
| `BACKUP_KEEP_DAYS` | `7` | Days of backups to keep |
| `UID` / `GID` | `1000` / `1000` | Owner of downloaded files (PC/Pi only; Unraid always uses 99:100) |

### API environment variables (advanced)

Most of these are better changed in the website's Settings, where changes apply immediately. Set the rest in the
`environment:` list of `tranga-api` only if you know you need them.

| Variable | Default | Description |
|---|---|---|
| `POSTGRES_HOST` | `tranga-pg:5432` | Database address (don't change) |
| `POSTGRES_DB` / `POSTGRES_USER` / `POSTGRES_PASSWORD` | `postgres` | Database name, user and password |
| `POSTGRES_COMMAND_TIMEOUT` / `POSTGRES_CONNECTION_TIMEOUT` | `60` / `30` | Database timeouts in seconds |
| `DOWNLOAD_LOCATION` | `/Manga` | Download folder inside the container (don't change) |
| `TZ` | | Time zone |
| `TRANGA_DISABLE_LOGIN` | `false` | `true` switches the login off (forgotten password) |
| `TRANGA_BACKUP_DIR` | `/backups` | Where the API looks for backups to show their status |
| `FLARESOLVERR_URL` | | FlareSolverr address, for sites behind Cloudflare |
| `HTTP_REQUEST_TIMEOUT` | `60` | Seconds before a website request times out |
| `REQUESTS_PER_MINUTE` | `90` | Request limit per website (don't change) |
| `WORKER_TIMEOUT` | `600` | Seconds a job may take before it's stopped |
| `CHECK_CHAPTERS_BEFORE_START` | `true` | Check which chapter files exist when starting (slow on big libraries) |
| `MATCH_EXACT_CHAPTER_NAME` | `true` | A chapter counts as downloaded only if its exact file name exists |
| `CREATE_COMICINFO_XML` | `true` | Put ComicInfo.xml in every `.cbz` |
| `ALWAYS_INCLUDE_VOLUME_IN_FILENAME` | `false` | Always put a volume in file names (`Vol. 0` if unknown) |
| `HOURS_BETWEEN_NEW_CHAPTERS_CHECK` | `3` | Default for the new-chapter check (Settings → Schedules overrides it) |
| `MINUTES_BETWEEN_NOTIFICATIONS` | `1` | Default for sending notifications (Settings → Schedules overrides it) |
| `PORT` | `6531` | API port inside the container (don't change) |

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## For developers

### Repositories and branch

- API: [croissantpigbear/tranga](https://github.com/croissantpigbear/tranga), branch **`tranga-plus`**
- Website: [croissantpigbear/tranga-website](https://github.com/croissantpigbear/tranga-website), branch **`tranga-plus`**

Each push to `tranga-plus` runs the unit tests (`Tests/`, xUnit) and, if they pass, builds and publishes
`ghcr.io/croissantpigbear/tranga-api:latest` and `ghcr.io/croissantpigbear/tranga-website:latest` for amd64 and arm64
(`.github/workflows/build-images.yml`).

### Build and run from source

```bash
git clone -b tranga-plus https://github.com/croissantpigbear/tranga.git
git clone -b tranga-plus https://github.com/croissantpigbear/tranga-website.git
cd tranga
cp .env.example .env
mkdir -p Manga settings postgres backups
docker compose -f docker-compose.local.yaml up -d --build
```

Run the tests: `dotnet test` (needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)).

### API

Interactive documentation: `http://<server>:9555/swagger`. Every endpoint is under `/v2`. When the login is on, send a
session cookie, `Authorization: Bearer <api key>` or `X-Api-Key: <api key>`.

Endpoints added by Tranga-Plus include `Auth`, `Backups`, `SettingsBackup`, `Calendar`, `LibraryImport`,
`SeriesPreferences`, `ChapterFiles` (incl. `Redownload`), `DownloadQueue`, `DownloadFailures`, `TaskSchedules`,
`LuaModules` (FMD2 sites, tests, title lists, updates, icons), `ExtraMetadata`, `ComicInfo`, `KavitaCovers`, `Naming`,
`DownloadLimits`, `Activity` and `ConnectionTests`.

### Running without Docker

Possible with the .NET 10 runtime and a PostgreSQL server. Settings are stored in `/usr/share/tranga-api` (Linux) or
`%appdata%/tranga-api` (Windows), and downloads go to `DOWNLOAD_LOCATION`. Docker is strongly recommended.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Credits and licence

- [Tranga](https://github.com/C9Glax/tranga) and [tranga-website](https://github.com/C9Glax/tranga-website) by
  [C9Glax](https://github.com/C9Glax) and contributors: the project Tranga-Plus is built on. 💙 Blåhaj 🦈
- [Free Manga Downloader 2 (FMD2)](https://github.com/dazedcat19/FMD2): the website modules Tranga-Plus can run.
- Series details: [AniList](https://anilist.co), [MangaUpdates](https://www.mangaupdates.com), [MangaDex](https://mangadex.org).
- Built with ASP.NET and EF Core, PostgreSQL / Npgsql, NLua (Lua 5.4), XPath2, Html Agility Pack, SixLabors.ImageSharp,
  PuppeteerSharp, Newtonsoft.Json, log4net and xUnit.

Distributed under the **GNU GPLv3** licence, like the original. See [LICENSE](LICENSE).

Tranga-Plus only downloads from websites you choose. Please support the authors and official releases of the series you enjoy.

<p align="right">(<a href="#readme-top">back to top</a>)</p>
