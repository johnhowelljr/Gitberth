# Lives in docker/, but its COPY lines expect to be built from the repo root.
FROM busybox:1.37
COPY site/ /www/
EXPOSE 8080
CMD ["httpd", "-f", "-p", "8080", "-h", "/www"]