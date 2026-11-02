# syntax=docker/dockerfile:1.7
FROM node:26.13.0-alpine@sha256:c6ad9e9c1d80f6eb607c26b4b0a132ff7a2344fad1c78ea3baa2a7d4c8de6d94 AS web
WORKDIR /web
COPY src/MarketplaceHub.Web/package.json src/MarketplaceHub.Web/package-lock.json ./
RUN npm install --global npm@11.12.1 \
 && npm ci --ignore-scripts
COPY src/MarketplaceHub.Web/ ./
RUN npm run build

FROM caddy:2.11.7@sha256:685ae3d7c34d9b5f8caeddc5d14051db7bbd88ff1f5a2fa0c16bd8ca328fb7af
COPY deploy/caddy/Caddyfile /etc/caddy/Caddyfile
RUN caddy adapt --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null
COPY --from=web /web/dist /srv
