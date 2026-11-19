#!/bin/bash

# Generates the self-signed ASP.NET certificate which local runs expect at
# ./certificates/aspnetapp.pfx. Run it with the password for the certificate,
# or source it to only define generate_certificates and call it later:
#
#   ./dev.sh <certificate-password>
#   source ./dev.sh && generate_certificates <certificate-password>
#
# DOTNET_SDK_VERSION can override the SDK image tag (default: 10.0).

function generate_certificates {
  local password=${1:-}
  if [ -z "$password" ]; then
    echo "Usage: $0 <certificate-password>" >&2
    return 1
  fi

  local sdk_version=${DOTNET_SDK_VERSION:-10.0}
  mkdir -p certificates

  # The password goes through the environment, not the command line, so it
  # doesn't show up in the process list.
  if docker run --rm --entrypoint "" \
      -v "$PWD/certificates:/certificates" \
      -e CERTIFICATE_PASSWORD="$password" \
      "mcr.microsoft.com/dotnet/sdk:$sdk_version" \
      sh -c 'dotnet dev-certs https --clean && dotnet dev-certs https -ep /certificates/aspnetapp.pfx -p "$CERTIFICATE_PASSWORD"'; then
    export CERTIFICATE_PASSWORD="$password"
    echo "success"
  else
    echo "failed to generate the certificate" >&2
    return 1
  fi
}

# Sourcing the script only defines the function; executing it generates the certificate.
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  generate_certificates "$@"
fi
