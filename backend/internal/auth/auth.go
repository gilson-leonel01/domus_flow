package auth

import (
	"errors"
	"github.com/golang-jwt/jwt/v5"
	"golang.org/x/crypto/bcrypt"
	"time"
)

type Claims struct {
	UserID      string `json:"uid"`
	HouseholdID string `json:"hid"`
	Role        string `json:"role"`
	jwt.RegisteredClaims
}

func Hash(v string) (string, error) {
	b, e := bcrypt.GenerateFromPassword([]byte(v), bcrypt.DefaultCost)
	return string(b), e
}
func Verify(hash, v string) bool {
	return bcrypt.CompareHashAndPassword([]byte(hash), []byte(v)) == nil
}
func Sign(secret, id, household, role string) (string, error) {
	c := Claims{UserID: id, HouseholdID: household, Role: role, RegisteredClaims: jwt.RegisteredClaims{ExpiresAt: jwt.NewNumericDate(time.Now().Add(24 * time.Hour)), IssuedAt: jwt.NewNumericDate(time.Now())}}
	return jwt.NewWithClaims(jwt.SigningMethodHS256, c).SignedString([]byte(secret))
}
func Parse(secret, raw string) (*Claims, error) {
	t, e := jwt.ParseWithClaims(raw, &Claims{}, func(t *jwt.Token) (any, error) {
		if t.Method != jwt.SigningMethodHS256 {
			return nil, errors.New("invalid signing method")
		}
		return []byte(secret), nil
	})
	if e != nil || !t.Valid {
		return nil, errors.New("invalid token")
	}
	c, ok := t.Claims.(*Claims)
	if !ok {
		return nil, errors.New("invalid claims")
	}
	return c, nil
}
