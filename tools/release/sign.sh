#!/usr/bin/env bash
# Signs a published release: downloads its zips and the SHA256SUMS CI wrote, checks every zip against
# it, signs SHA256SUMS with the release key and uploads SHA256SUMS.sig. Runs on the maintainer's machine,
# never in CI, so a compromised CI cannot sign. The key is RELEASE_SIGN_KEY (tools/paths.py); its public
# half is packaging/allowed_signers, which players verify against.
#
# Usage: GITEA_TOKEN=... tools/release/sign.sh vX.Y.Z
# First time: ssh-keygen -t ed25519 -f ~/.ssh/cx-release -C cx-releases
#             echo "cx-releases $(cat ~/.ssh/cx-release.pub)" > packaging/allowed_signers   (and commit it)
set -euo pipefail
tag="${1:?usage: tools/release/sign.sh vX.Y.Z}"
: "${GITEA_TOKEN:?set GITEA_TOKEN (a Gitea token with write access to the repository)}"
root=$(cd "$(dirname "$0")/../.." && pwd)
api_base=https://git.westgate.pw/api/v1/repos/ShadowsOverWestgate/nwncxee
key=$(python3 "$root/tools/paths.py" RELEASE_SIGN_KEY)
signers="$root/packaging/allowed_signers"
[[ -f "$key" ]] || { echo "no release key at $key (see the header of this script)" >&2; exit 1; }
[[ -s "$signers" ]] || { echo "packaging/allowed_signers is missing (see the header of this script)" >&2; exit 1; }

api() { curl -sSf -H "Authorization: token $GITEA_TOKEN" "$@"; }
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
release=$(api "$api_base/releases/tags/$tag")
id=$(jq -r .id <<<"$release")

jq -r '.assets[] | select(.name != "SHA256SUMS.sig") | "\(.name)\t\(.browser_download_url)"' <<<"$release" |
  while IFS=$'\t' read -r name url; do api -o "$work/$name" "$url"; done
cd "$work"
[[ -f SHA256SUMS ]] || { echo "$tag has no SHA256SUMS asset" >&2; exit 1; }
for z in *.zip; do
  grep -q "  $z\$" SHA256SUMS || { echo "$z is not listed in SHA256SUMS" >&2; exit 1; }
done
sha256sum -c SHA256SUMS

ssh-keygen -Y sign -f "$key" -n file SHA256SUMS
ssh-keygen -Y verify -f "$signers" -I cx-releases -n file -s SHA256SUMS.sig < SHA256SUMS

api "$api_base/releases/$id/assets" | jq -r '.[] | select(.name=="SHA256SUMS.sig") | .id' |
  while read -r aid; do api -X DELETE "$api_base/releases/$id/assets/$aid"; done
api -X POST -F "attachment=@SHA256SUMS.sig" "$api_base/releases/$id/assets?name=SHA256SUMS.sig" > /dev/null
echo "signed $tag"
