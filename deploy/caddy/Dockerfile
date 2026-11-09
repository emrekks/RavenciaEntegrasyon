# syntax=docker/dockerfile:1.7
FROM node:26.13.0-alpine@sha256:8996bb5821d62a8fd9bb27c1cb07462f3fdce6e47d640c6a881ead0b064ce216 AS web
WORKDIR /web
COPY src/MarketplaceHub.Web/package.json src/MarketplaceHub.Web/package-lock.json ./
RUN npm install --global npm@11.12.1 \
 && npm ci --ignore-scripts
COPY src/MarketplaceHub.Web/ ./
RUN npm run build

FROM caddy:2.11.9@sha256:0c2243efcf437ab7f7a978f1027fcf0d4f9be8a75df27b1b3fbc5dbe0ffa8cf0
# brotli-libs 1.2.0 in the base image: CVE-2026-111497 (see Dockerfile.production).
RUN apk upgrade --no-cache brotli-libs
COPY deploy/caddy/Caddyfile /etc/caddy/Caddyfile
RUN caddy adapt --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null
COPY --from=web /web/dist /srv
