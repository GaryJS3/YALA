# YALA Roadmap and Progress

This file tracks the product direction and implementation status. Update it when work starts, ships, or is deliberately deferred. Keep the scope focused on a fast, self-hosted shared grocery list.

## Shipped

- [x] .NET 10 Blazor Server application with SQLite, EF Core migrations, and a single-container Docker deployment.
- [x] Persistent `/data` volume for the database, images, and Identity data-protection keys; `/health` endpoint.
- [x] First-run account and household setup, local sign-in, and closed public registration after setup.
- [x] Administrator user management for creating accounts, resetting passwords, and guarded account deletion.
- [x] Multiple independent households, membership management, household switching, saved default household, rename, archive, and restore.
- [x] Household-scoped catalog items, categories, favorites, aliases, product variants, barcodes, and optional images.
- [x] Shopping list quick add/search, quantity changes, check/uncheck, recent/frequent suggestions, and clear-checked purchase history.
- [x] Household-specific stores, item availability, store assignment, aisle/offer notes, and manual price history with last and average prices.
- [x] Item and store list views; store assignment remains distinct from store availability.
- [x] Dedicated store detail pages for renaming, pausing, offers, preferred-store choices, and preserved price history.
- [x] Live list-change notifications within the running application instance.
- [x] Reverse-proxy deployment guidance, backup/restore steps, and Dockhand Git-stack deployment notes.
- [x] Focused automated tests for shopping-list and household isolation.

## Next up

- [ ] Add broader service and UI acceptance coverage for account setup, household switching/default selection, catalog search and aliases, repeated adds, check/clear purchase history, stores, and prices.
- [ ] Add explicit cross-household isolation coverage for catalog, variants, barcodes, stores/offers, prices, purchase history, favorites, and notifications.
- [ ] Verify production deployment and upgrade/restore procedures against a real Docker host, including reverse-proxy WebSocket behavior.

## Planned backlog

These are future-friendly ideas from the original product brief; they are not commitments or current implementation requirements.

- [x] Camera barcode scanning with manual barcode entry fallback.
- [x] Product lookup across the Open Facts databases and UPCitemdb, merging the richest available details.
- [ ] Installable PWA experience.
- [ ] Receipt entry and estimated trip totals.
- [ ] Store aisle ordering and per-store category ordering.
- [ ] Import/export and copying items between households as independent records.
- [ ] REST API, if a concrete integration need emerges.
- [ ] Household-specific themes or icons.

## Out of scope

Pantry inventory, expiration tracking, meal planning, recipes, nutrition tracking, chores, calendars, grocery ordering, retailer integrations, coupons, loyalty cards, receipt OCR, budgeting, automatic price scraping, global product/store catalogs, complex household roles, public registration, and SaaS billing are outside YALA's intended scope unless the product direction is explicitly revisited.

## Progress notes

- 2026-09-19: Fixed store-image delivery by allowing the household image endpoint to serve the stores folder. Store logos retain their original aspect ratio and transparent pixels across detail, list, and availability views.
- 2026-09-19: Saved catalog items in both shopping-list views now open their catalog detail page when the item content is clicked, while ad-hoc entries and list controls remain unchanged.
- 2026-09-19: Stores can now have household-scoped uploaded logos or pictures. Store images are validated and persisted alongside existing item images, and appear on store cards, store detail pages, shopping-list availability indicators, and item availability controls.
- 2026-09-19: Consolidated application build information into the navigation rail, added the Eastern build date and time, and simplified the shopping-list heading copy.
- 2026-09-19: Replaced the blocking Blazor reconnect dialog with a compact, accessible status banner at the top of the viewport and set automatic reconnect attempts to a consistent three-second interval.
- 2026-09-19: Exact products can now be removed from the item editor after inline confirmation. Removal archives the product and disables its store availability while preserving barcodes, offers, prices, and purchase history.
- 2026-09-19: Condensed the item page's read-only Store availability section to show only stores that carry the item. Edit mode continues to show every store with availability controls.
- 2026-09-19: Added a store selector to the shopping list's Stores view so one store section can be shown at a time, with All stores remaining the default. The selected view and readable store name are reflected in query parameters (for example, `?view=stores&store=Walmart`) so the state survives reloads and can be shared as a link.
- 2026-09-19: Shopping-list rows with multiple exact products now identify the preferred product directly. In a different store's section, the alternate-store note also identifies a store carrying the preferred exact product.
- 2026-09-19: Removed store-assignment dropdowns from saved catalog items on the main shopping list. Saved items now show their configured available stores as read-only labels; store selection remains available for ad-hoc list entries.
- 2026-09-19: Catalog items without their own uploaded image now display the preferred exact-product image when available, falling back to the first named product image. A directly uploaded item image continues to take priority.
- 2026-09-19: Shopping-list item rows now distinguish multiple exact products and show the stores carrying each one. In the Stores view, an item available from multiple stores appears in each relevant store section, but each row shows only the exact products available at that store (including when there is just one); alternate availability and preferred-store status are called out without repeating other stores' products or store names beside prices. Store-grouped rows omit redundant assignment controls when availability is already configured, while unmatched typed entries remain ad-hoc list items with optional store assignment instead of being silently saved to the catalog. The Catalog page lists current ad-hoc entries and can promote them into full catalog items without losing their list state, or delete them after inline confirmation.
- 2026-09-19: Added per-product store availability to the item page. Exact products can now be assigned to one or more stores during creation or from their product cards, allowing store-specific brands and package sizes while preserving offer and price history when availability changes.
- 2026-09-19: Fixed image persistence so local uploads use the same resolved data directory as the database and image endpoint. Exact products created from barcode lookup now save a validated local copy of the lookup photo instead of discarding the preview URL.
- 2026-09-19: Moved store editing and store-specific item management to dedicated detail pages. Removing an item from a store now marks the offer unavailable instead of deleting its price history.
- 2026-09-19: Added administrator-only user management. The first account is the administrator; administrators can create local accounts, set replacement passwords, and delete accounts with safeguards for their own account, the last administrator, and sole household members.
- 2026-09-18: Added camera barcode scanning and manual UPC/EAN/GTIN entry to exact products. Product details are filled from a merged Open Food Facts, Open Pet Food Facts, Open Beauty Facts, Open Products Facts, and UPCitemdb lookup, while remaining editable before saving.
- 2026-09-18: Replaced inline catalog item editing/details with a dedicated item page. The page has distinct view and edit modes for attributes, photos, aliases, exact products, favorite/archive state, and per-store availability checkboxes; catalog rows now navigate to it.
- 2026-09-18: Fixed logout from the main header by supplying the relative return URL expected by the Identity logout endpoint, avoiding an invalid `~//Account/Login` local redirect and its production error page.
- 2026-09-18: Fixed stale form submissions by synchronizing action-driven fields on input in Catalog, Stores, and Household screens. The production reverse proxy was also returning 404 for `/_framework/blazor.web.js`, leaving server-side buttons inert; the app now serves the framework script through `/blazor.web.js` and explicitly includes ASP.NET web assets in publish output. Live acceptance verified empty-store validation, adding a store, saving a catalog item, and quick-adding a list item on deployed commit `db6c547`.
- 2026-09-18: Added the application version and a unique assembly build identifier to the main-layout footer (and desktop rail) so users can confirm which build their browser loaded.
- 2026-09-18: Initial roadmap created from the YALA product brief and current repository implementation. The next-up list distinguishes missing acceptance coverage and deployment validation from shipped application features.
