import { useEffect, useState } from 'react'

export const TOKEN_KEY = 'google_token'

export type GoogleUser = {
  email: string
  name: string
  picture: string
}

export function readToken(): string | null {
  return localStorage.getItem(TOKEN_KEY)
}

export function clearToken() {
  localStorage.removeItem(TOKEN_KEY)
}

export function useGoogleAuth() {
  const [user, setUser] = useState<GoogleUser | null>(null)
  const [ready, setReady] = useState(false)

  useEffect(() => {
    let cancelled = false
    const token = readToken()

    async function load() {
      if (!token) {
        window.location.replace('/')
        return
      }
      try {
        const response = await fetch('/api/user/me', {
          headers: { Authorization: `Bearer ${token}` },
        })
        if (cancelled) {
          return
        }
        if (!response.ok) {
          clearToken()
          window.location.replace('/')
          return
        }
        const data = await response.json() as { email?: string; name?: string; picture?: string }
        setUser({
          email: data.email ?? '',
          name: data.name ?? '',
          picture: data.picture ?? '',
        })
      } catch {
        if (!cancelled) {
          window.location.replace('/')
        }
      } finally {
        if (!cancelled) {
          setReady(true)
        }
      }
    }

    function onLost() {
      clearToken()
      window.location.replace('/')
    }

    window.addEventListener('dusey-auth-lost', onLost)
    void load()
    return () => {
      cancelled = true
      window.removeEventListener('dusey-auth-lost', onLost)
    }
  }, [])

  return { user, ready }
}
