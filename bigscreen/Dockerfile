# bifrost-exchange/bigscreen/Dockerfile
# Stage 1 — Vite build
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npx tsc -b && npm run build

# Stage 2 — nginx serves the static bundle on port 4173 so the compose
# mapping "4173:4173" lands on a listening socket without host remap.
FROM nginx:1.27-alpine AS serve
COPY --from=build /app/dist /usr/share/nginx/html
# Override the default server block to listen on 4173 (not 80) and
# fall back to index.html for any unmatched path (SPA-friendly).
RUN printf 'server {\n  listen 4173;\n  root /usr/share/nginx/html;\n  index index.html;\n  location / {\n    try_files $uri $uri/ /index.html;\n  }\n}\n' > /etc/nginx/conf.d/default.conf
EXPOSE 4173
