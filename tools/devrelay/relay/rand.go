package relay

import "crypto/rand"

// Indirected so a test can make randomness fail without touching crypto/rand.
var randRead = rand.Read
