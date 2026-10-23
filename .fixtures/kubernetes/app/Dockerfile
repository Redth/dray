FROM busybox:1.37
RUN mkdir /www && echo "hello from myapp" > /www/index.html
EXPOSE 8080
CMD ["httpd","-f","-p","8080","-h","/www"]
