# SmartHotel.Web image: builds the production Angular bundle with Node and
# serves it with nginx, which renders /config.json from API_BASE_URL and
# WEB_ENVIRONMENT at container start. Neither input has a default here; the
# deployment supplies both.

FROM node:20.20.2-alpine3.23 AS build
WORKDIR /app
ENV NG_CLI_ANALYTICS=false PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
# The npm bundled with the base image ships a vulnerable tar. The build stage
# installs with this pinned npm 11 release instead, which supports Node 20 and
# whose bundled dependencies are audited separately from the lockfile.
RUN npm install -g npm@11.21.3 --no-audit --no-fund
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

# Guards the two inputs and rewrites the API origin in the browser's form
# before the template is rendered. The image's entrypoint sources this file
# after 15-local-resolvers.envsh and before 20-envsubst-on-templates.sh.
COPY --chmod=0755 <<'EOF' /docker-entrypoint.d/15-smarthotel-web-inputs.envsh
#!/bin/sh
# The nginx template places API_BASE_URL and WEB_ENVIRONMENT inside
# single-quoted nginx strings, where a single quote ends the string, a dollar
# sign starts a variable and a backslash starts an escape, and nginx rejects a
# parameter of about 4 KB or more. An input that holds any of those characters
# or is longer than 1000 bytes is set to empty, so nginx still starts and the
# template serves it as an empty API base URL, which the SPA rejects, or as
# Production. nginx reads bytes, so the limit counts bytes whatever the
# shell's locale: 1000 ASCII characters fit, but only 500 two-byte UTF-8 ones.
# The message names the variable and the reason and never prints the value.
# The entrypoint's shell sources this file under set -eu, so it never exits,
# never fails outside a condition, never changes shell options and leaves no
# variables of its own behind.

if [ -n "${API_BASE_URL-}" ] && [ "$(printf '%s' "$API_BASE_URL" | wc -c)" -gt 1000 ]; then
  echo "15-smarthotel-web-inputs.envsh: API_BASE_URL is longer than 1000 bytes, this image's limit for it; it is served as empty" >&2
  API_BASE_URL=
  export API_BASE_URL
elif [ -n "${API_BASE_URL-}" ] && case "$API_BASE_URL" in *\'* | *\$* | *\\*) true ;; *) false ;; esac; then
  echo "15-smarthotel-web-inputs.envsh: API_BASE_URL holds a single quote, a dollar sign or a backslash, which nginx cannot read here; it is served as empty" >&2
  API_BASE_URL=
  export API_BASE_URL
fi

# The browser sends the SPA's requests to the origin its URL parser reads from
# API_BASE_URL, which the template cannot compute: the parser decodes a
# percent-encoded host, reads a host whose last label is a number as an IPv4
# address written in decimal, octal or hexadecimal parts, and drops a
# zero-padded default port. So the origin of a kept http or https value is
# rewritten here as that parser serializes it: the scheme and host in lower
# case, an IPv4 address in dotted decimal, no empty user name or password,
# and the port without leading zeros, left out when it is the scheme's
# default. The path, query and fragment are kept as they are, whatever bytes
# they hold. /config.json then serves, and the Content-Security-Policy names,
# the origin the browser calls, and the SPA accepts or rejects the value just
# as it would the input.
# A host that holds a non-ASCII character, as a raw byte or percent-encoded,
# is one the parser maps to an ASCII form through Unicode IDNA processing,
# punycode for a domain name, which this image cannot compute. Such a value is
# set to empty, whatever else its host holds, with a message that names the
# variable and the reason and never the value, so the SPA shows its boot-error
# state instead of starting with an API origin the Content-Security-Policy
# does not name.
# Every other value is left as it is: one that does not parse, whose
# authority holds an ASCII control character, or that has a user name or
# password, and one whose host is an IPv6 literal, holds an ASCII character
# other than a letter, digit, hyphen or dot, or has an empty label, which no
# source expression can name. The rewrite adds at most 16 bytes, which nginx
# still reads.
if [ -n "${API_BASE_URL-}" ]; then
  API_BASE_URL=$(if LC_ALL=C awk '
    BEGIN {
      v = ENVIRON["API_BASE_URL"]
      for (i = 32; i < 127; i++) ascii = ascii sprintf("%c", i)
      for (i = 1; i < 32; i++) control = control sprintf("%c", i)
      control = control sprintf("%c", 127)
      sub("^ +", "", v)
      scheme = tolower(substr(v, 1, index(v, ":") - 1))
      if (scheme != "http" && scheme != "https") exit 1
      v = substr(v, length(scheme) + 2)
      sub("^/+", "", v)
      stop = match(v, "[/?#]") ? RSTART : length(v) + 1
      authority = substr(v, 1, stop - 1)
      rest = substr(v, stop)
      for (i = 1; i <= length(authority); i++) if (index(control, substr(authority, i, 1))) exit 1
      sub("^:?@", "", authority)
      if (index(authority, "@") || index(authority, "[") || index(authority, "]")) exit 1
      colon = index(authority, ":")
      host = colon ? substr(authority, 1, colon - 1) : authority
      port = colon ? substr(authority, colon + 1) : ""
      if (host == "" || port !~ "^[0-9]*$") exit 1
      for (i = 1; i <= length(host); i++) {
        c = substr(host, i, 1)
        if (!index(ascii, c) || (c == "%" && tolower(substr(host, i + 1, 2)) ~ "^[89a-f][0-9a-f]$")) exit 2
      }
      hex = "0123456789abcdef"
      name = ""
      for (i = 1; i <= length(host); i++) {
        c = substr(host, i, 1)
        if (c == "%") {
          pair = tolower(substr(host, i + 1, 2))
          if (pair !~ "^[0-9a-f][0-9a-f]$") exit 1
          code = (index(hex, substr(pair, 1, 1)) - 1) * 16 + index(hex, substr(pair, 2, 1)) - 1
          c = (code < 32 || code > 126) ? "" : substr(ascii, code - 31, 1)
          i += 2
        }
        if (c !~ "^[A-Za-z0-9.-]$") exit 1
        name = name c
      }
      host = tolower(name)
      dot = ""
      if (host ~ "[^.][.]$") {
        host = substr(host, 1, length(host) - 1)
        dot = "."
      }
      n = split(host, label, ".")
      for (i = 1; i <= n; i++) if (label[i] == "") exit 1
      if (label[n] ~ "^([0-9]+|0x[0-9a-f]*)$") {
        if (n > 4) exit 1
        address = 0
        for (i = 1; i <= n; i++) {
          part = label[i]
          radix = 10
          if (part ~ "^0x") {
            radix = 16
            part = substr(part, 3)
          } else if (part ~ "^0.") {
            radix = 8
            part = substr(part, 2)
          }
          sub("^0+", "", part)
          if (length(part) > 11) exit 1
          value = 0
          for (j = 1; j <= length(part); j++) {
            digit = index(substr(hex, 1, radix), substr(part, j, 1))
            if (!digit) exit 1
            value = value * radix + digit - 1
          }
          if (i < n && value > 255) exit 1
          if (i == n && value >= 256 ^ (5 - n)) exit 1
          address = (i < n) ? (address * 256 + value) : (address * 256 ^ (5 - n) + value)
        }
        host = sprintf("%d.%d.%d.%d", int(address / 16777216), int(address / 65536) % 256, int(address / 256) % 256, address % 256)
        dot = ""
      }
      if (port != "") {
        sub("^0+", "", port)
        if (port == "") port = "0"
        if (length(port) > 5 || port + 0 > 65535) exit 1
        if (port + 0 == (scheme == "https" ? 443 : 80)) port = ""
      }
      printf "%s://%s%s%s%s", scheme, host, dot, (port == "" ? "" : ":" port), rest
    }
  ' </dev/null; then :; else case $? in
    (2) echo "15-smarthotel-web-inputs.envsh: API_BASE_URL has a host with a non-ASCII character, which this image cannot write in the browser's ASCII form; give the host in that form, such as its punycode (xn--) spelling; it is served as empty" >&2 ;;
    (*) printf '%s' "$API_BASE_URL" ;;
  esac; fi; printf x)
  API_BASE_URL=${API_BASE_URL%x}
  export API_BASE_URL
fi

if [ -n "${WEB_ENVIRONMENT-}" ] && [ "$(printf '%s' "$WEB_ENVIRONMENT" | wc -c)" -gt 1000 ]; then
  echo "15-smarthotel-web-inputs.envsh: WEB_ENVIRONMENT is longer than 1000 bytes, this image's limit for it; it is served as empty" >&2
  WEB_ENVIRONMENT=
  export WEB_ENVIRONMENT
elif [ -n "${WEB_ENVIRONMENT-}" ] && case "$WEB_ENVIRONMENT" in *\'* | *\$* | *\\*) true ;; *) false ;; esac; then
  echo "15-smarthotel-web-inputs.envsh: WEB_ENVIRONMENT holds a single quote, a dollar sign or a backslash, which nginx cannot read here; it is served as empty" >&2
  WEB_ENVIRONMENT=
  export WEB_ENVIRONMENT
fi
EOF

# A Windows checkout with CRLF line endings gives the file above CRLF endings,
# which the entrypoint's shell cannot source.
RUN sed -i 's/\r$//' /docker-entrypoint.d/15-smarthotel-web-inputs.envsh

EXPOSE 80
