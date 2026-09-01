package relay

import (
	"crypto/rand"
	"math/big"
	"strings"
)

// Crockford base32: no I, L, O or U, so a code survives being read aloud over
// voice chat (PLAN.md §1.1).
const alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

// CodeLength is the number of characters in an issued code.
const CodeLength = 8

// NewCode issues a fresh session code.
func NewCode() (string, error) {
	var sb strings.Builder
	sb.Grow(CodeLength)
	max := big.NewInt(int64(len(alphabet)))
	for i := 0; i < CodeLength; i++ {
		n, err := rand.Int(rand.Reader, max)
		if err != nil {
			return "", err
		}
		sb.WriteByte(alphabet[n.Int64()])
	}
	return sb.String(), nil
}

// NormaliseCode applies the forgiving inbound normalisation of §1.1: case,
// surrounding whitespace, spaces, hyphens and underscores are ignored, and the
// characters Crockford treats as confusable fold together.
//
// The mod deliberately does not implement this — it passes through whatever the
// player typed and lets the relay sort it out — so this is the only place the
// rules live.
func NormaliseCode(raw string) string {
	var sb strings.Builder
	sb.Grow(len(raw))
	for _, r := range strings.ToUpper(strings.TrimSpace(raw)) {
		switch r {
		case ' ', '\t', '-', '_':
			continue
		case 'I', 'L':
			sb.WriteByte('1')
		case 'O':
			sb.WriteByte('0')
		default:
			sb.WriteRune(r)
		}
	}
	return sb.String()
}

// NewToken issues a reclaim token. It is a secret: never log it (§5.3, §8).
func NewToken() (string, error) {
	buf := make([]byte, 24)
	if _, err := rand.Read(buf); err != nil {
		return "", err
	}
	const hex = "0123456789abcdef"
	out := make([]byte, len(buf)*2)
	for i, b := range buf {
		out[i*2] = hex[b>>4]
		out[i*2+1] = hex[b&0x0f]
	}
	return string(out), nil
}
