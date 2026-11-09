FROM busybox:1.37
COPY www/ /www/
EXPOSE 8080
CMD ["httpd", "-f", "-p", "8080", "-h", "/www"]