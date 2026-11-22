#!/bin/sh
# The rig's Railpack test app. Railpack's shell provider starts it as `sh start.sh`; Perl comes with Debian's slim image,
# which Railpack's runtime image is. It answers {"marker": …} on / and the PORT it got on /env.
echo "gbe2e railpack app listening on ${PORT:-8080}"
exec perl -MSocket -e '
  my $port = $ENV{PORT} || 8080;
  socket(my $s, PF_INET, SOCK_STREAM, 6) or die "socket: $!";
  setsockopt($s, SOL_SOCKET, SO_REUSEADDR, pack("l", 1));
  bind($s, sockaddr_in($port, INADDR_ANY)) or die "bind: $!";
  listen($s, 16) or die "listen: $!";
  open(my $f, "<", "marker.txt"); my $m = <$f> // ""; chomp $m;
  while (accept(my $c, $s)) {
    my $line = <$c> // ""; my ($path) = $line =~ m{^\S+ (\S+)};
    while (my $h = <$c>) { last if $h =~ /^\r?$/ }
    my $body = ($path // "") eq "/env" ? "{\"PORT\":\"$port\"}" : "{\"marker\":\"$m\"}";
    print $c "HTTP/1.0 200 OK\r\nContent-Type: application/json\r\nContent-Length: " . length($body) . "\r\nConnection: close\r\n\r\n$body";
    close $c;
  }'
