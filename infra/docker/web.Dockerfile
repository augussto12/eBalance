# syntax=docker/dockerfile:1.7
# Frontend React compilado con lockfile y servido por Nginx sin privilegios. Sin variables VITE_* secretas.
ARG NODE_IMAGE=node:24.22.1-alpine3.24
ARG NGINX_IMAGE=nginxinc/nginx-unprivileged:1.30.6-alpine3.24

FROM ${NODE_IMAGE} AS build
WORKDIR /web
COPY src/web/package.json src/web/package-lock.json ./
RUN --mount=type=secret,id=extra_ca,required=false \
    if [ -s /run/secrets/extra_ca ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/extra_ca; fi; \
    npm ci --no-audit --no-fund
COPY src/web/ ./
RUN npm run build

FROM ${NGINX_IMAGE}
COPY infra/nginx/web/default.conf /etc/nginx/conf.d/default.conf
COPY infra/nginx/web/security-headers.conf /etc/nginx/security-headers.conf
COPY --from=build /web/dist /usr/share/nginx/html
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --retries=3 CMD wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1
