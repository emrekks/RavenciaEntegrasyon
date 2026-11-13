#!/usr/bin/env bash
# One-command setup of the off-site backup copy (security plan Y3) on the server, run by someone with sudo after the
# owner has created the age key pair and authorised Google Drive on their own computer
# (docs/CI-CD-DEPLOY.md "Sunucu dışı yedek"):
#
#   sudo bash deploy/scripts/configure-offsite-backup.sh [--replace] age1...   < token.json
#
# The Drive token (the JSON `rclone authorize "drive" --drive-scope drive.file` prints) comes on stdin, never on the
# command line: pipe or redirect it, or run without a redirect and paste it at the hidden prompt, then Ctrl-D. The
# marker lines around it ("Paste the following...", "<---End paste") may be pasted along. With no token at all, an
# existing deploy/secrets/rclone.conf that already holds a "gdrive" drive.file remote is kept.
#
# It writes, each mode 0600 and owned by root, into deploy/secrets/:
#   backup_age_recipient.txt   the owner's age PUBLIC key
#   rclone.conf                remote "gdrive", type drive, scope drive.file, the token
#   backup_offsite_remote.txt  gdrive:ravencia-yedek
# then creates the Drive folder, runs install-backup-timer.sh (which installs ravencia-offsite-copy.timer once these
# exist), takes the first copy through ravencia-offsite-copy.service and shows the timer and the Drive folder.
#
# Safe to run again with the same settings: a file whose content is already right is left as it is (rclone.conf
# counts as the same while it is a gdrive drive.file remote with the same refresh token, since rclone itself
# rewrites the access token). A file that exists with OTHER content (e.g. from an earlier, half-finished setup) is
# not overwritten: the script lists them and stops (exit 3) before changing anything. --replace overwrites them and
# keeps each old file next to it as <name>.bak-<UTC time>. The token is never printed or logged.
#
# Test hooks: RAVENCIA_OFFSITE_SECRETS_DIR, RAVENCIA_CONFIGURE_SKIP_SYSTEM=1 (no root check, no units, no first copy).
set -Eeuo pipefail
umask 077

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_root="$(cd -- "$script_dir/../.." && pwd -P)"
secrets_dir="${RAVENCIA_OFFSITE_SECRETS_DIR:-$repository_root/deploy/secrets}"
skip_system="${RAVENCIA_CONFIGURE_SKIP_SYSTEM:-}"
remote_name=gdrive
destination="$remote_name:ravencia-yedek"

usage() {
  echo "Usage: sudo bash deploy/scripts/configure-offsite-backup.sh [--replace] <age public key age1...> < token.json" >&2
  exit 64
}

replace=""
recipient=""
for argument in "$@"; do
  case "$argument" in
    --replace) replace=yes ;;
    -h|--help) usage ;;
    -*) echo "Unknown option $argument." >&2; usage ;;
    *) [[ -z "$recipient" ]] || usage; recipient="$argument" ;;
  esac
done
[[ -n "$recipient" ]] || usage
if [[ "$recipient" == *AGE-SECRET-KEY* ]]; then
  echo "That is an age PRIVATE key. It never belongs on the server; give the public key (age1...) instead." >&2
  exit 1
fi
[[ "$recipient" =~ ^age1[0-9a-z]{58}$ ]] \
  || { echo "'$recipient' is not an age public key (age1 followed by 58 characters)." >&2; exit 1; }

if [[ -z "$skip_system" && "$EUID" -ne 0 ]]; then
  echo "Run it as root: sudo bash deploy/scripts/configure-offsite-backup.sh ..." >&2
  exit 1
fi
missing=()
for tool in age rclone; do command -v "$tool" >/dev/null || missing+=("$tool"); done
if ((${#missing[@]})); then
  echo "Not installed: ${missing[*]}. Install first: sudo apt-get install -y age rclone" >&2
  exit 1
fi
echo "age $(age --version 2>/dev/null || echo '?'), $(rclone version 2>/dev/null | head -n 1 || echo 'rclone ?')"

# The token: everything between the first "{" and the last "}" of stdin, on one line.
if [[ -t 0 ]]; then
  echo "Paste the token JSON from 'rclone authorize' (it is not shown), then press Enter and Ctrl-D:" >&2
  stty -echo 2>/dev/null || true
  input="$(cat)" || input=""
  stty echo 2>/dev/null || true
  echo >&2
else
  input="$(cat)" || input=""
fi
input="$(tr -d '\r\n' <<<"$input")"
token=""
if [[ "$input" == *"{"*"}"* ]]; then
  token="{${input#*\{}"
  token="${token%\}*}}"
fi
unset input
refresh_token_of() { sed -n 's/.*"refresh_token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' <<<"$1" | head -n 1; }
if [[ -n "$token" ]]; then
  [[ -n "$(refresh_token_of "$token")" ]] \
    || { echo "The token on stdin has no refresh_token: copy the whole JSON line 'rclone authorize' printed." >&2; exit 1; }
fi

recipient_file="$secrets_dir/backup_age_recipient.txt"
remote_file="$secrets_dir/backup_offsite_remote.txt"
rclone_config="$secrets_dir/rclone.conf"

# The gdrive section of an rclone.conf: "type|scope|refresh token" (empty when there is no such section).
gdrive_summary() {
  local section="" type="" scope="" token_line=""
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    if [[ "$line" =~ ^\[(.*)\]$ ]]; then section="${BASH_REMATCH[1]}"; continue; fi
    [[ "$section" == "$remote_name" ]] || continue
    case "$line" in
      type*=*) type="$(sed 's/^[^=]*=[[:space:]]*//' <<<"$line")" ;;
      scope*=*) scope="$(sed 's/^[^=]*=[[:space:]]*//' <<<"$line")" ;;
      token*=*) token_line="$line" ;;
    esac
  done <"$1"
  [[ -n "$type" ]] || return 0
  printf '%s|%s|%s\n' "$type" "$scope" "$(refresh_token_of "$token_line")"
}

existing_drive=""
[[ -s "$rclone_config" ]] && existing_drive="$(gdrive_summary "$rclone_config")"
if [[ -z "$token" ]]; then
  if [[ "$existing_drive" =~ ^drive\|drive\.file\|.+ ]]; then
    echo "No token on stdin: keeping the gdrive remote already in deploy/secrets/rclone.conf."
  else
    echo "No token on stdin and deploy/secrets/rclone.conf holds no usable gdrive drive.file remote." >&2
    echo "Give the JSON from 'rclone authorize \"drive\" --drive-scope drive.file' on stdin (< token.json)." >&2
    exit 1
  fi
fi

# What each file should hold, and whether the one on disk already does: new | same | differs.
recipient_content="$recipient"
remote_content="$destination"
rclone_content=""
[[ -z "$token" ]] || rclone_content="$(printf '[%s]\ntype = drive\nscope = drive.file\ntoken = %s' "$remote_name" "$token")"

state_of() {  # <file> <wanted content>
  [[ -e "$1" ]] || { echo new; return; }
  if [[ "$(tr -d '\r' <"$1" | sed -e 's/[[:space:]]*$//' -e '/^$/d')" == "$2" ]]; then echo same; else echo differs; fi
}
recipient_state="$(state_of "$recipient_file" "$recipient_content")"
remote_state="$(state_of "$remote_file" "$remote_content")"
if [[ -z "$token" ]]; then
  rclone_state=same
elif [[ ! -e "$rclone_config" ]]; then
  rclone_state=new
elif [[ "$existing_drive" == "drive|drive.file|$(refresh_token_of "$token")" ]]; then
  rclone_state=same
else
  rclone_state=differs
fi

conflicts=()
[[ "$recipient_state" == differs ]] && conflicts+=("backup_age_recipient.txt holds: $(head -c 200 "$recipient_file" | tr '\n' ' ')")
[[ "$remote_state" == differs ]] && conflicts+=("backup_offsite_remote.txt holds: $(head -c 200 "$remote_file" | tr '\n' ' ')")
if [[ "$rclone_state" == differs ]]; then
  if [[ -n "$existing_drive" ]]; then
    conflicts+=("rclone.conf has a gdrive remote with another token or scope (${existing_drive%|*})")
  else
    conflicts+=("rclone.conf exists without a gdrive remote")
  fi
fi
if ((${#conflicts[@]})); then
  if [[ -z "$replace" ]]; then
    echo "deploy/secrets already holds off-site backup settings that differ from these (an earlier setup?):" >&2
    printf '  - %s\n' "${conflicts[@]}" >&2
    echo "Nothing was changed. Check with whoever set them up; to overwrite them (old files kept as" >&2
    echo "<name>.bak-<time>) run the same command again with --replace." >&2
    exit 3
  fi
  echo "Replacing (--replace):"
  printf '  - %s\n' "${conflicts[@]}"
fi

# Writes <file> atomically (temporary file in the same directory, then rename), 0600, root-owned.
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
write_secret() {  # <file> <state> <content>
  local file="$1" state="$2" content="$3" name="${1##*/}" temporary
  if [[ "$state" == same ]]; then
    [[ -e "$file" ]] && chmod 0600 "$file"
    echo "  unchanged  deploy/secrets/$name"
    return
  fi
  if [[ "$state" == differs ]]; then cp -p -- "$file" "$file.bak-$stamp"; chmod 0600 "$file.bak-$stamp"; fi
  temporary="$(mktemp "$file.new.XXXXXX")"
  printf '%s\n' "$content" >"$temporary"
  chmod 0600 "$temporary"
  [[ -n "$skip_system" ]] || chown root:root "$temporary"
  mv -f -- "$temporary" "$file"
  echo "  $( [[ "$state" == new ]] && echo written || echo replaced )    deploy/secrets/$name"
}
if [[ ! -d "$secrets_dir" ]]; then
  install -d -m 0700 "$secrets_dir"
fi
echo "deploy/secrets:"
write_secret "$recipient_file" "$recipient_state" "$recipient_content"
write_secret "$rclone_config" "$rclone_state" "$rclone_content"
write_secret "$remote_file" "$remote_state" "$remote_content"
unset token rclone_content

export RCLONE_CONFIG="$rclone_config"
# Proves the token works and creates the folder (rclone may write a refreshed token back into rclone.conf).
if ! rclone mkdir "$destination" 2>"$secrets_dir/.rclone-error.$$"; then
  echo "rclone could not reach Google Drive with this token:" >&2
  # rclone's own error lines name the failure; they do not contain the token.
  grep -v -i 'token' "$secrets_dir/.rclone-error.$$" | tail -n 5 >&2 || true
  rm -f -- "$secrets_dir/.rclone-error.$$"
  echo "Have the owner run 'rclone authorize \"drive\" --drive-scope drive.file' again and rerun this with the new token." >&2
  exit 1
fi
rm -f -- "$secrets_dir/.rclone-error.$$"
chmod 0600 "$rclone_config"
echo "Google Drive reachable; folder $destination ready."

if [[ -n "$skip_system" ]]; then
  echo "RAVENCIA_CONFIGURE_SKIP_SYSTEM set: units and the first copy skipped."
  rclone lsjson "$destination"
  exit 0
fi

echo
echo "Installing the units (deploy/scripts/install-backup-timer.sh):"
bash "$repository_root/deploy/scripts/install-backup-timer.sh"
systemctl is-enabled --quiet ravencia-offsite-copy.timer \
  || { echo "ravencia-offsite-copy.timer was not installed; see the output above." >&2; exit 1; }

echo
echo "First copy (ravencia-offsite-copy.service; up to 2 hours for a large set):"
since="$(date '+%Y-%m-%d %H:%M:%S')"
copy_status=0
systemctl start ravencia-offsite-copy.service || copy_status=$?
journalctl --no-pager -o cat -u ravencia-offsite-copy.service --since "$since" | tail -n 20
chmod 0600 "$rclone_config"

echo
systemctl list-timers --no-pager 'ravencia-offsite-*'
echo
echo "Google Drive $destination:"
rclone lsjson "$destination"
if ((copy_status)); then
  echo "The first copy FAILED (status $copy_status); the lines above say why. The timer stays installed and retries." >&2
  exit 1
fi
echo "Off-site backup copy configured."
