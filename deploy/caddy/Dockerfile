# syntax=docker/dockerfile:1.7
FROM node:26.13.0-alpine@sha256:c6ad9e9c1d80f6eb607c26b4b0a132ff7a2344fad1c78ea3baa2a7d4c8de6d94 AS web
WORKDIR /web
COPY src/MarketplaceHub.Web/package.json src/MarketplaceHub.Web/package-lock.json ./
RUN npm install --global npm@11.12.1 \
 && npm ci --ignore-scripts
COPY src/MarketplaceHub.Web/ ./
RUN npm run build

FROM caddy:2.11.7@sha256:36c24ad91a7ff7bc4c87fe22bfa8df3c4b18da0773d73fc12ee2c52b98c0bb7e
# brotli-libs 1.2.0 in the base image: CVE-2026-111497 (see Dockerfile.production).
RUN apk upgrade --no-cache brotli-libs
COPY deploy/caddy/Caddyfile /etc/caddy/Caddyfile
RUN caddy adapt --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null
COPY --from=web /web/dist /srv
