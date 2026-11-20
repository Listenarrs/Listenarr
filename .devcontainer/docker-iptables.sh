#!/bin/sh
# Run once when the dev container is created (onCreateCommand).
#
# The docker-in-docker feature makes Docker use iptables-legacy. A host whose
# kernel only has nftables cannot serve it, and the Docker daemon of the dev
# container then never starts: switch to iptables-nft there.
set -e

if ! sudo iptables-legacy -t nat -L -n > /dev/null 2>&1; then
	sudo update-alternatives --set iptables /usr/sbin/iptables-nft
	sudo update-alternatives --set ip6tables /usr/sbin/ip6tables-nft
fi

# The feature's entrypoint retries the daemon for about 30 seconds and picks
# the change up on its next attempt; start the daemon again if it gave up.
seconds=0
until docker info > /dev/null 2>&1; do
	seconds=$((seconds + 1))
	if [ "$seconds" -eq 45 ]; then
		sudo /usr/local/share/docker-init.sh true
	elif [ "$seconds" -ge 90 ]; then
		echo 'The Docker daemon did not start; see /tmp/dockerd.log.' >&2
		exit 1
	fi
	sleep 1
done
