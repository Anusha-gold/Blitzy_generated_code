# SmartHotel.Web image: builds the production Angular bundle with Node and
# serves it with nginx, which renders /config.json from API_BASE_URL and
# WEB_ENVIRONMENT at container start. Neither input has a default here; the
# deployment supplies both.

FROM node:20.20.2-alpine3.23 AS build
WORKDIR /app
ENV NG_CLI_ANALYTICS=false PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY angular.json tsconfig.json tsconfig.app.json ./
# Only the build's inputs are copied: the entry files angular.json names and the
# components under src/app, which the .dockerignore allowlist limits to their
# .ts, .html and .css files, so no local dotenv, override, environment, secrets,
# certificate or key file is copied.
COPY src/index.html src/main.ts src/styles.css ./src/
COPY src/app ./src/app
RUN npx ng build --configuration production

FROM nginx:1.30.7-alpine3.24
RUN rm -rf /usr/share/nginx/html/*
COPY --from=build /app/dist/smart-hotel-web/browser/ /usr/share/nginx/html/
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template

# The image's template renderer substitutes every defined environment variable
# a template names, nginx variables included, unless this filter limits it.
# Only the two inputs are substituted, so nginx variables survive whatever else
# the environment defines.
ENV NGINX_ENVSUBST_FILTER='^(API_BASE_URL|WEB_ENVIRONMENT)$'

# Guards the two inputs before the template is rendered. The image's entrypoint
# sources this file after 15-local-resolvers.envsh and before
# 20-envsubst-on-templates.sh.
COPY --chmod=0755 <<'EOF' /docker-entrypoint.d/15-smarthotel-web-inputs.envsh
#!/bin/sh
# The nginx template places API_BASE_URL and WEB_ENVIRONMENT inside
# single-quoted nginx strings, where a single quote ends the string, a dollar
# sign starts a variable and a backslash starts an escape, and nginx rejects a
# parameter of about 4 KB or more. An input that holds any of those characters
# or is longer than 1000 characters is set to empty, so nginx still starts and
# the template serves it as an empty API base URL, which the SPA rejects, or as
# Production. The message names the variable and never prints its value.
# The entrypoint's shell sources this file, so it never exits, never changes
# shell options and leaves no variables of its own behind.

if [ -n "${API_BASE_URL-}" ] && {
  [ "${#API_BASE_URL}" -gt 1000 ] ||
    case "$API_BASE_URL" in *\'* | *\$* | *\\*) true ;; *) false ;; esac
}; then
  echo "15-smarthotel-web-inputs.envsh: API_BASE_URL holds a single quote, a dollar sign or a backslash, or is longer than 1000 characters, which nginx cannot read; it is served as empty" >&2
  API_BASE_URL=
  export API_BASE_URL
fi

if [ -n "${WEB_ENVIRONMENT-}" ] && {
  [ "${#WEB_ENVIRONMENT}" -gt 1000 ] ||
    case "$WEB_ENVIRONMENT" in *\'* | *\$* | *\\*) true ;; *) false ;; esac
}; then
  echo "15-smarthotel-web-inputs.envsh: WEB_ENVIRONMENT holds a single quote, a dollar sign or a backslash, or is longer than 1000 characters, which nginx cannot read; it is served as empty" >&2
  WEB_ENVIRONMENT=
  export WEB_ENVIRONMENT
fi
EOF

# A Windows checkout with CRLF line endings gives the file above CRLF endings,
# which the entrypoint's shell cannot source.
RUN sed -i 's/\r$//' /docker-entrypoint.d/15-smarthotel-web-inputs.envsh

EXPOSE 80
