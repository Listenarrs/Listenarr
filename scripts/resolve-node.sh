#!/usr/bin/env sh

# Some Windows Git clients launch hooks with a reduced PATH that omits Node.
if ! command -v node >/dev/null 2>&1 && [ -d "/c/Program Files/nodejs" ]; then
  export PATH="/c/Program Files/nodejs:$PATH"
fi

resolve_node() {
  if command -v node >/dev/null 2>&1; then
    command -v node
    return
  fi

  if [ -x "/c/Program Files/nodejs/node.exe" ]; then
    echo "/c/Program Files/nodejs/node.exe"
    return
  fi

  echo "node command not found" >&2
  exit 127
}
