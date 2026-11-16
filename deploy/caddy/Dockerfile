# syntax=docker/dockerfile:1.7
FROM node:26.13.1-alpine@sha256:6bd9ab43c0ec0190ca7b2d193906cb8b7edf6e7f4afa43ddeccd6452710bffb5 AS web
WORKDIR /web
COPY src/MarketplaceHub.Web/package.json src/MarketplaceHub.Web/package-lock.json ./
RUN npm install --global npm@11.12.1 \
 && npm ci --ignore-scripts
COPY src/MarketplaceHub.Web/ ./
RUN npm run build

FROM caddy:2.11.8@sha256:4fcafa98afd1c2e642e353e3c5cf9ddbe892fcee14bc990e7ba2c5b78e51f9cf
# brotli-libs 1.2.0 in the base image: CVE-2026-111497 (see Dockerfile.production).
RUN apk upgrade --no-cache brotli-libs
COPY deploy/caddy/Caddyfile /etc/caddy/Caddyfile
RUN caddy adapt --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null
COPY --from=web /web/dist /srv
