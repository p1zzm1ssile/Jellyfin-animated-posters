FROM nginx:1.27-alpine

LABEL org.opencontainers.image.title="Library Cover Maker" \
      org.opencontainers.image.description="Make animated cover art for your Jellyfin libraries" \
      org.opencontainers.image.source="https://github.com/p1zzm1ssile/Library-Cover-Maker" \
      net.unraid.docker.icon="https://raw.githubusercontent.com/p1zzm1ssile/Library-Cover-Maker/main/web/icon.png" \
      net.unraid.docker.webui="http://[IP]:[PORT:8097]/"

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY web/ /usr/share/nginx/html/
RUN mkdir -p /media

EXPOSE 8097
HEALTHCHECK --interval=60s --timeout=5s CMD wget -q -O /dev/null http://127.0.0.1:8097/ || exit 1
