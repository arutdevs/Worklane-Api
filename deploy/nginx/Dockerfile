# Local edge image. The local stack builds this so the edge config is in the
# image. The deploy compose file still bind-mounts default.conf instead.
FROM nginxinc/nginx-unprivileged:1.28-alpine
COPY default.conf /etc/nginx/conf.d/default.conf
