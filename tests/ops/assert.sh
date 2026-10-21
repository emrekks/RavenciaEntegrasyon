#!/usr/bin/env bash
# Tiny assertion helpers for the operations script tests (tests/ops/run.sh).
# Source this file; each test file is a standalone bash script.

ops_failures=0

pass() { printf '  ok   %s\n' "$1"; }
fail() { printf '  FAIL %s\n' "$1"; ops_failures=$((ops_failures + 1)); }

assert_eq() {
  local expected="$1" actual="$2" name="$3"
  if [[ "$expected" == "$actual" ]]; then pass "$name"; else fail "$name (expected '$expected', got '$actual')"; fi
}

assert_contains() {
  local haystack="$1" needle="$2" name="$3"
  if [[ "$haystack" == *"$needle"* ]]; then pass "$name"; else fail "$name (missing '$needle' in: ${haystack:0:400})"; fi
}

assert_not_contains() {
  local haystack="$1" needle="$2" name="$3"
  if [[ "$haystack" != *"$needle"* ]]; then pass "$name"; else fail "$name (unexpected '$needle')"; fi
}

assert_dir() { if [[ -d "$1" ]]; then pass "$2"; else fail "$2 (missing directory $1)"; fi; }
assert_no_dir() { if [[ ! -e "$1" ]]; then pass "$2"; else fail "$2 (still present: $1)"; fi; }

finish() {
  if ((ops_failures)); then
    printf '%s: %d failure(s)\n' "$1" "$ops_failures"
    exit 1
  fi
  printf '%s: all passed\n' "$1"
}
