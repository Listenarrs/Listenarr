#!/bin/sh
set -eu

groupadd --system listenarr
useradd --system --gid listenarr --home-dir /nonexistent --shell /usr/sbin/nologin --no-create-home listenarr
