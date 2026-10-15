<span id="readme-top"></span>
<div align="center">

  <h1 align="center">Tranga v2</h1>
  <p align="center">
    Automatic Manga and Metadata downloader 
  </p>
  
  ![GitHub License](https://img.shields.io/github/license/C9glax/tranga)

  <p align="center">
    <b>Fork</b> of <a href="https://github.com/C9Glax/tranga">C9Glax/tranga</a> (<code>cuttingedge</code>) with extra features: any website from
    <a href="https://github.com/dazedcat19/FMD2">FMD2</a> Lua modules, full ComicInfo.xml, Kavita covers, download limits,
    activity logs and more. See <a href="#whats-different-in-this-fork">What's different in this fork</a>.<br/>
    Website (frontend): <a href="https://github.com/croissantpigbear/tranga-website">croissantpigbear/tranga-website</a>
  </p>

</div>

<!-- ABOUT THE PROJECT -->
## About The Project

Tranga can download Chapters and Metadata from "Scanlation" sites such as

- [MangaDex.org](https://mangadex.org/) (Multilingual), built in
- **Any website with an [FMD2](https://github.com/dazedcat19/FMD2) Lua module** (hundreds of sites), added by uploading the module
  on the website's FMD2 page. Tested with MangaFire, Asura Scans and WeebCentral.

and trigger a library-scan with [Komga](https://komga.org/) and [Kavita](https://www.kavitareader.com/).  
Notifications can be sent to your devices using [Gotify](https://gotify.net/), [LunaSea](https://www.lunasea.app/) or [Ntfy](https://ntfy.sh/
), or any other service that can use REST Webhooks.

## What this program does and does *not* do

*DOES*: Download Images from a Website.<br />
*DOES*: Create Archives with those images.<br />

_**how**?_

Tranga (this repository) is a REST-API and worker in one. Tranga provides REST-Endpoints to configure workers (Jobs).
Requests include searches for Manga, creating and starting Jobs such as downloading available chapters.
For available endpoints check `http(s)://<hostedInstance>/swagger` or `API/openapi/API_v2.json`

**This repository** _**does not**_ include a frontend. A frontend can take many forms, such as a website:

[croissantpigbear/tranga-website](https://github.com/croissantpigbear/tranga-website) (matches this fork)

[tranga-website](https://github.com/C9Glax/tranga-website) (Original, if you use the `docker-compose.yaml` in this repo it will spin up an instance for this)

[tranga-yacobGUI](https://github.com/yacob841/tranga-yacobGUI) (Third Party)

When downloading a chapter (meaning the images that make-up the manga) from a Website, Tranga will
additionally try and fetch Metadata from the same website or enhance it from third-party sources.
Downloaded images can be jpeg-compressed and/or made black and white to save on diskspace
(measured at least a 50% reduction in size, without a significant loss of quality).

Tranga will then package the contents of each chapter in a `.cbz`-archive and place it in a common folder per Manga.
If specified, Tranga will then notify library-Managers such as [Komga](https://komga.org/) and [Kavita](https://www.kavitareader.com/) to trigger a scan for new
chapters. Tranga can also send notifications to your devices via third-party services such as [Gotify](https://gotify.net/), [Ntfy](https://ntfy.sh/),
or any other REST Webhook.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## What's different in this fork

### New: websites from FMD2 Lua modules
- Upload a module (plus any templates/utils it needs) from [FMD2](https://github.com/dazedcat19/FMD2/tree/master/lua) and the site works like a built-in one: search, add by link, chapters, downloads
- Modules run in a sandboxed Lua 5.4 with time and request limits; no file or system access
- FMD2 compatibility: XPath 2.0 and FMD2's JSON queries, HTTP, JSON, crypto, templates and utils, `MODULE.Storage` kept between runs
- Presents itself as a browser like FMD2 does, retries Cloudflare-blocked pages through FlareSolverr
- Per site: switch on/off, test on a manga link (with request log), build a title list for searching
- Adding by link tries every site that handles the link, uploaded modules first
- All 625 FMD2 site modules load

### Built-in websites
- The outdated AsuraComic, Mangaworld and WeebCentral connectors are removed; MangaDex stays built in
- Manga linked to them are kept and moved automatically to an uploaded module for the same site
- After switching a manga to another website, chapters not downloaded yet are downloaded from the new one

### Kavita and other readers
- Full `ComicInfo.xml` in every chapter: series, number, volume, writers, artists, publisher, genres, summary, age rating, status, links
- Rewrite ComicInfo.xml of existing chapters in the background; preview per chapter
- Send Tranga's downloaded covers to Kavita (automatically every 15 minutes or on request, optionally locked)
- Series folder naming scheme, with a preview and a tool to rename existing folders
- Connection tests for Komga/Kavita and notification services

### Downloads and performance
- Simultaneous downloads limited per website (default 2) as well as in total
- Number of jobs at once is adjustable; the API reports CPU count and memory for hardware presets
- Settings apply immediately instead of after a restart

### Information
- Extra manga details (MangaUpdates, MangaDex content rating) stored in the database
- Chapter files: sizes per manga, delete a single chapter file, disk summary, next check time
- Activity: recent log lines (last 5,000 in memory, downloadable) and running/scheduled jobs with readable names
- Refresh a manga's chapters and cover on request

### New endpoints

| Area | Endpoints (under `/v2`) |
|---|---|
| FMD2 websites | `GET LuaModules`, `POST LuaModules/Upload`, `DELETE LuaModules/{Kind}/{FileName}`, `PATCH LuaModules/Sites/{SiteName}/Enabled/{Enabled}`, `POST LuaModules/Sites/{SiteName}/TitleList`, `POST LuaModules/Sites/{SiteName}/Test` |
| ComicInfo.xml | `GET ComicInfo/Status`, `GET ComicInfo/Chapter/{ChapterId}`, `POST ComicInfo/Rewrite`, `POST ComicInfo/Rewrite/Manga/{MangaId}` |
| Kavita covers | `GET KavitaCovers`, `PATCH KavitaCovers`, `POST KavitaCovers/Push`, `POST KavitaCovers/Push/{MangaId}` |
| Download limits | `GET DownloadLimits`, `PATCH DownloadLimits` |
| Activity | `GET Activity/Logs`, `GET Activity/Logs/Download`, `GET Activity/Workers` |
| Chapter files | `GET ChapterFiles/Manga/{MangaId}`, `DELETE ChapterFiles/{ChapterId}`, `GET ChapterFiles/Summary`, `GET ChapterFiles/Schedule` |
| Extra details | `GET ExtraMetadata`, `GET ExtraMetadata/{MangaId}`, `POST ExtraMetadata/{MangaId}/Refresh` |
| Naming | `GET/PATCH Naming/SeriesFolderScheme`, `POST Naming/Preview`, `GET Naming/SeriesFolders/Plan`, `POST Naming/SeriesFolders/Rename` |
| Connection tests | `POST ConnectionTests/LibraryConnector/{LibraryConnectorId}`, `POST ConnectionTests/NotificationConnector/{Name}` |
| Manga | `POST Manga/{MangaId}/Refresh` |

Full details at `http://<url>/swagger` (`openapi/API_v2.json` in this repo doesn't include the new endpoints yet).

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Screenshots

This repository has no frontend. Use [croissantpigbear/tranga-website](https://github.com/croissantpigbear/tranga-website) for the features of this fork, or the original [tranga-website](https://github.com/C9Glax/tranga-website).

## Inspiration:

Because [Kaizoku](https://github.com/oae/kaizoku) was relying on [mangal](https://github.com/metafates/mangal) and mangal
hasn't received bugfixes for its issues with Titles not showing up, or throwing errors because of illegal characters,
there were no alternatives for automatic downloads. However, [Kaizoku](https://github.com/oae/kaizoku) certainly had a great Web-UI.

That is why I wanted to create my own project, in a language I understand, and that I am able to maintain myself.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Endpoint Documentation

Checkout `openapi/API_v2.json`

The container also spins up a Swagger site at `http://<url>/swagger`.

## Built With

**💙 [Blåhaj](https://www.ikea.com/us/en/p/blahaj-soft-toy-shark-90373590/) 🦈**
- [ASP.NET](https://dotnet.microsoft.com/en-us/apps/aspnet)
  - [EF Core](https://learn.microsoft.com/en-us/ef/core/)
- [PostgreSQL](https://www.postgresql.org/about/licence/)
  - [Ngpsql](https://github.com/npgsql/npgsql/blob/main/LICENSE)
- [Swagger](https://github.com/domaindrivendev/Swashbuckle.AspNetCore/blob/master/LICENSE)
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json/blob/master/LICENSE.md)
- [Sixlabors.ImageSharp](https://docs-v2.sixlabors.com/articles/imagesharp/index.html#license)
- [Html Agility Pack (HAP)](https://github.com/zzzprojects/html-agility-pack/blob/master/LICENSE)
- [Puppeteersharp](https://www.puppeteersharp.com/)
  - [Puppeteer](https://github.com/puppeteer/puppeteer) 
  - [Chromium](https://www.chromium.org/Home/)
- [Soenneker.Utils.String.NeedlemanWunsch](https://github.com/soenneker/soenneker.utils.string.needlemanwunsch/blob/main/LICENSE)
- [Jikan](https://jikan.moe/)
  - [Jikan.Net](https://github.com/Ervie/jikan.net)
- [BuildInformation](https://github.com/linkdotnet/BuildInformation)
- [GitInfo](https://github.com/devlooped/GitInfo)
- [Log4Net](https://logging.apache.org/log4net/index.html)
- [xUnit](https://xunit.net/index.html?tabs=cs)
- [NLua](https://github.com/NLua/NLua) / [KeraLua](https://github.com/NLua/KeraLua) (Lua 5.4, for FMD2 modules)
- [XPath2](https://github.com/StefH/XPath2.Net) (XPath 2.0, for FMD2 modules)
- [Free Manga Downloader 2 (FMD2)](https://github.com/dazedcat19/FMD2): the website modules this fork can run

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Star History

<a href="https://star-history.dera.page/#c9glax/tranga&Date">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://star-history.dera.page/svg?repos=c9glax/tranga&type=Date&theme=dark" />
   <source media="(prefers-color-scheme: light)" srcset="https://star-history.dera.page/svg?repos=c9glax/tranga&type=Date" />
   <img alt="Star History Chart" src="https://star-history.dera.page/svg?repos=c9glax/tranga&type=Date" />
 </picture>
</a>

<!-- GETTING STARTED -->
## Getting Started

<p align="right">(<a href="#readme-top">back to top</a>)</p>

### Docker

This fork has no published images, so build them yourself (AMD64 and ARM64, e.g. Raspberry Pi 4 with a 64-bit OS):

```
git clone -b claude https://github.com/croissantpigbear/tranga.git
git clone -b claude https://github.com/croissantpigbear/tranga-website.git
docker build --platform linux/amd64 -t tranga-claude-api:latest ./tranga                # or linux/arm64
docker build --platform linux/amd64 -t tranga-claude-website:latest ./tranga-website    # or linux/arm64
```
Then use `tranga-claude-api:latest` and `tranga-claude-website:latest` as the images in `docker-compose.yaml`.
Add a volume for the database (`/var/lib/postgresql/data`) so it survives the container being recreated.

To add websites, upload FMD2 modules on the website's FMD2 page. They are stored in `lua/` inside the settings folder
(`/usr/share/tranga-api`); copy that folder to move them to another machine.

An example `docker-compose.yaml` is provided. Mount `/Manga` to wherever you want your chapters (`.cbz`-Archives)
downloaded (where Komga/Kavita can access them for example).  
The file also includes [tranga-website](https://github.com/C9Glax/tranga-website) as frontend. For its configuration refer to the
[Tranga-Website Repository](https://github.com/C9Glax/tranga-website) README.

| Environment Variable              | default          | Description                                                                                                      |
|-----------------------------------|------------------|------------------------------------------------------------------------------------------------------------------|
| PORT                              | `6531`           | Port for the API Endpoints (Don't change this unless you know what you are doing)                                |
| POSTGRES_HOST                     | `tranga-pg:5432` | host-address of postgres database (Don't change!)                                                                |
| POSTGRES_DB                       | `postgres`       | name of database                                                                                                 |
| POSTGRES_USER                     | `postgres`       | username used for database authentication                                                                        |
| POSTGRES_PASSWORD                 | `postgres`       | password used for database authentication                                                                        |
| DOWNLOAD_LOCATION                 | `/Manga`         | Target-Directory for Downloads (Path inside the container! Don't change!)                                        |
| FLARESOLVERR_URL                  | <empty>          | URL of Flaresolverr-Instance                                                                                     |
| POSTGRES_COMMAND_TIMEOUT          | `60`             | [Timeout of Postgres-commands](https://www.npgsql.org/doc/connection-string-parameters.html?q=Command%20Timeout) |
| POSTGRES_CONNECTION_TIMEOUT       | `30`             | Timeout for postgres-databaes connection                                                                         |
| CHECK_CHAPTERS_BEFORE_START       | `true`           | Whether to update database downloaded chapters column on startup (takes a while)                                 |
| MATCH_EXACT_CHAPTER_NAME          | `true`           | Match the stored filename exactly with the filename on disk when checking if a chapter is downloaded             |
| CREATE_COMICINFO_XML              | `true`           | Whether to include ComicInfo.xml in .cbz-Archives                                                                |
| ALWAYS_INCLUDE_VOLUME_IN_FILENAME | `false`          | Override to always include a volume in filenames (default as `Vol. 0`)                                           |
| HTTP_REQUEST_TIMEOUT              | `10`             | Request timeout for Mangaconnectors                                                                              |
| REQUESTS_PER_MINUTE               | `90`             | Maximum requests per minute for Mangaconnectors (Don't change)                                                   |
| MINUTES_BETWEEN_NOTIFICATIONS     | `1`              | Interval at which Tranga checks if notifications need to be sent.                                                |
| HOURS_BETWEEN_NEW_CHAPTERS_CHECK  | `3`              | Interval at which Tranga checks if there are new chapters for a manga                                            |
| WORKER_TIMEOUT                    | `600`            | Seconds a worker can take before being forcefully cancelled                                                      |

Download limits, jobs at once, Kavita covers and ComicInfo.xml can also be changed from the website's Settings page.

### Bare-Metal

While not intended, if your machine can run .NET (and is AMD64, ARM64, ARMv7), it can run Tranga without docker.

Configuration-Files are stored at
- Linux `/usr/share/tranga-api`
- Windows `%appdata%/tranga-api`

Downloads (default) are stored in (see `DOWNLOAD_LOCATION`)
- Linux `/Manga`
- Windows `%currentDirectory%/Downloads`

### Prerequisits

[.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

<!-- CONTRIBUTING -->
## Contributing

If you want to contribute, please feel free to fork and create a Pull-Request!

Please read [CONTRIBUTING](CONTRIBUTING.md)

<!-- LICENSE -->
## License

Distributed under the GNU GPLv3  License. See [LICENSE](https://github.com/C9Glax/tranga/blob/main/LICENSE) for more information.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- ACKNOWLEDGMENTS -->
## Acknowledgments

* [Choose an Open Source License](https://choosealicense.com)
* [Best-README-Template](https://github.com/othneildrew/Best-README-Template/tree/master)
* [C9Glax](https://github.com/C9Glax) and contributors for Tranga
* The [FMD2](https://github.com/dazedcat19/FMD2) contributors for the website modules
* [Shields.io](https://shields.io/)

<p align="right">(<a href="#readme-top">back to top</a>)</p>
