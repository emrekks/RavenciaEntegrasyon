import { useState } from 'react'

// The initials of a person's name: the first letters of the first and the last word (at most 2), upper-cased the Turkish way
// (i becomes İ). An empty name gives ''.
export function profileInitials(name: string) {
  const words = name.trim().split(/\s+/).filter(Boolean)
  if (!words.length) return ''
  const letters = words.length === 1 ? [words[0]] : [words[0], words[words.length - 1]]
  return letters.map(word => Array.from(word)[0]).join('').toLocaleUpperCase('tr-TR')
}

// One avatar for the sidebar and the phone menu: the profile picture when there is one (round, cover; the initials when it
// does not load), otherwise the initials. The session carries no picture field yet, so `imageUrl` is not passed today.
export function UserAvatar({ name, imageUrl, className, fallback = '' }: { name: string; imageUrl?: string | null; className?: string; fallback?: string }) {
  const [failed, setFailed] = useState<string | null>(null)
  const showImage = Boolean(imageUrl) && failed !== imageUrl
  return <span className={className} aria-hidden="true">{showImage ? <img src={imageUrl!} alt="" onError={() => setFailed(imageUrl!)} /> : profileInitials(name) || fallback}</span>
}
