# syntax=docker/dockerfile:1.7
FROM node:26.12.0-alpine@sha256:6f08a523443ddedf52941874c203475b0ad503e859ad70cb26ab8be9a517dbd5 AS web
WORKDIR /web
COPY src/MarketplaceHub.Web/package.json src/MarketplaceHub.Web/package-lock.json ./
RUN npm install --global npm@11.12.1 \
 && npm ci --ignore-scripts
COPY src/MarketplaceHub.Web/ ./
RUN npm run build

FROM caddy:2.11.7@sha256:504a44d7d74bd2bc28a66afc4774912fc21223ca8a67734af1c54ab2f999b489
COPY deploy/caddy/Caddyfile /etc/caddy/Caddyfile
RUN caddy adapt --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null
COPY --from=web /web/dist /srv
