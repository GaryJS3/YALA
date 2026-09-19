# YALA

YALA - Yet Another List App

YALA is a small, self-hosted grocery list for one or more households. Each household has an independent catalog, list, stores, prices, and purchase history. Anyone in a household can manage it.

The shopping list is the home screen. Familiar items stay in the catalog, so you can add them again without recreating them.

See [the roadmap and progress tracker](docs/ROADMAP.md) for shipped features and planned work.

## Run with Docker Compose

Docker Compose is the recommended self-hosted setup. It runs one ASP.NET Core container, stores the SQLite database, uploaded images, and Identity signing keys in one persistent volume, and listens on loopback port 8080 for a reverse proxy.

```sh
docker compose up -d --build
docker compose ps
```

Open `http://127.0.0.1:8080` locally, or configure a reverse proxy to send HTTPS traffic to that address. On first launch, create the first username, password, and household. Public registration closes once that account exists. Household members can create additional local accounts and add existing YALA users to their household.

The container listens on port `8080`. The named volume `yala-data` is mounted at `/data` and contains:

```text
/data/yala.db
/data/images/
/data/keys/
```

YALA applies committed EF Core migrations at startup. It exposes `/health` for container and proxy health checks.

### Deploy from Git with Dockhand

Create a Dockhand Git stack for `https://github.com/GaryJS3/YALA`, tracking branch `main`. Set the Compose file path to `compose.yaml` and the context directory to the repository root (`.`). The Compose service builds from the root `Dockerfile`, uses `pull_policy: build`, and disables build cache.

In the Git stack's deploy options, enable **Build images on deploy**. Dockhand keeps this setting per stack and otherwise may run Compose without `--build`; the Compose file cannot enable that Dockhand option by itself. New commits in the stack directory then trigger a deployment with a fresh image build. Enable **Force redeployment** only if unchanged commits should also redeploy on every scheduled or webhook sync. See the [Dockhand Git integration guide](https://dockhand.pro/manual/).

### Reverse proxy

YALA expects TLS to terminate at the reverse proxy. For Caddy:

```caddyfile
lists.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

Caddy handles WebSocket upgrades used by Blazor Server. For Nginx, proxy the `Host`, `X-Forwarded-For`, and `X-Forwarded-Proto` headers and enable HTTP/1.1 WebSocket upgrades. The Compose port binds to `127.0.0.1` so the application port is not exposed directly to the network.

### Back up and restore

Stop YALA before copying its volume so SQLite has flushed its write-ahead log. The backup includes the database, images, and persistent Identity keys.

```sh
docker compose stop yala
mkdir -p backups
docker run --rm -v yala-data:/data -v "$PWD/backups:/backup" alpine \
  tar -czf /backup/yala-data-$(date +%F).tar.gz -C /data .
docker compose start yala
```

To restore, stop YALA, extract the chosen backup into the `yala-data` volume, then start it again. Keep a copy of the current volume until the restored list has been checked.

### Upgrade

Back up `/data` before upgrading. Then rebuild and start the container:

```sh
docker compose up -d --build
docker compose logs --tail=100 yala
curl --fail http://127.0.0.1:8080/health
```

Database migrations run as part of startup. Keep the same `yala-data` volume between versions.

## Local development

Requirements: .NET 10 SDK.

```sh
dotnet tool restore
dotnet restore YALA.slnx
dotnet build YALA.slnx
dotnet test YALA.slnx
dotnet run --project src/YALA/YALA.csproj
```

The first visit to the local address shows first-run setup. Outside Docker, YALA stores its database and data-protection keys in the operating system's local application data directory under `YALA`. Set `YALA_DATA_DIR` to use a different directory; the database filename remains `yala.db`.

Create a migration when the EF model changes:

```sh
dotnet ef migrations add DescribeTheChange --project src/YALA/YALA.csproj --output-dir Data/Migrations
```

Migrations are applied automatically at startup. For a local manual update, run:

```sh
dotnet ef database update --project src/YALA/YALA.csproj
```

## What works today

- Initial local account and household setup, login, logout, and remember-me.
- Multiple independent households, a visible household switcher, a separately saved default, household member management, rename/archive/restore, and per-household category settings.
- Quick add, search, favorites, recent and frequent purchases, fractional quantities, checked items, and clear-checked purchase history.
- Catalog editing, categories, favorites, archive/restore, aliases, exact product variants, multiple barcodes, optional item/product images, and camera/manual barcode lookup.
- Household-specific stores with custom ordering, generic and variant-specific offers, aisle notes, and manual price history with last and average prices.
- Items and Stores list views without duplicating shopping-list rows. Store assignment remains separate from store availability.
- Household-scoped notifications for live list changes inside the single running instance.

Images are limited to JPEG, PNG, or WebP files up to 4 MB. Camera scanning requires HTTPS (or localhost), camera permission, and a browser with the Barcode Detection API; manual barcode entry is always available. Product lookup queries the four Open Facts databases and UPCitemdb, then merges the richest available result. Lookup services receive the barcode and the server's public IP address.

## Data and privacy

Identity accounts are global to one YALA installation. Catalog, shopping, store, price, and purchase records belong to one household. Application queries and composite SQLite foreign keys enforce that boundary. There are no household roles: every member can manage that household.

YALA does not seed grocery items or stores. Each new household receives its own set of default categories. There is no SMTP, external identity provider, or open registration after initial setup.
