#!/bin/bash
# Setup dedicated Keycloak realm + client for IronGate
# Prereqs: Keycloak running on http://localhost:8080 with admin/admin

set -euo pipefail

KEYCLOAK_URL="http://localhost:8080"
ADMIN_USER="admin"
ADMIN_PASS="admin"
REALM="irongate"
CLIENT_ID="irongate-gateway"
CLIENT_SECRET="irongate-secret-change-me"
TEST_USER="irongate-user"
TEST_PASS="password123"

echo "==> Getting admin access token..."
ADMIN_TOKEN=$(curl -s -X POST "${KEYCLOAK_URL}/realms/master/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "username=${ADMIN_USER}" \
  -d "password=${ADMIN_PASS}" \
  -d "grant_type=password" \
  -d "client_id=admin-cli" | jq -r '.access_token')

if [ -z "$ADMIN_TOKEN" ] || [ "$ADMIN_TOKEN" = "null" ]; then
  echo "ERROR: failed to get admin token"
  exit 1
fi

auth_header() { echo "Authorization: Bearer ${ADMIN_TOKEN}"; }

echo "==> Creating realm '${REALM}'..."
curl -s -X POST "${KEYCLOAK_URL}/admin/realms" \
  -H "$(auth_header)" \
  -H "Content-Type: application/json" \
  -d "{
    \"realm\": \"${REALM}\",
    \"enabled\": true,
    \"registrationAllowed\": false,
    \"sslRequired\": \"none\"
  }" > /dev/null || true  # may already exist

echo "==> Creating client '${CLIENT_ID}'..."
CLIENT_JSON=$(cat <<EOF
{
  "clientId": "${CLIENT_ID}",
  "name": "IronGate Gateway Client",
  "enabled": true,
  "clientAuthenticatorType": "client-secret",
  "secret": "${CLIENT_SECRET}",
  "protocol": "openid-connect",
  "publicClient": false,
  "serviceAccountsEnabled": false,
  "authorizationServicesEnabled": false,
  "defaultClientScopes": ["profile", "email"],
  "redirectUris": ["http://localhost:5023/*"],
  "webOrigins": ["*"],
  "attributes": {
    "token.response.type.bearer": "introspect",
    "use.refresh.tokens": "false"
  }
}
EOF
)
EXISTING_CLIENT=$(curl -s -X GET "${KEYCLOAK_URL}/admin/realms/${REALM}/clients?clientId=${CLIENT_ID}" \
  -H "$(auth_header)" | jq -r '.[0].id // empty')

if [ -z "$EXISTING_CLIENT" ]; then
  CLIENT_RESPONSE=$(curl -s -X POST "${KEYCLOAK_URL}/admin/realms/${REALM}/clients" \
    -H "$(auth_header)" \
    -H "Content-Type: application/json" \
    -d "$CLIENT_JSON")
  CLIENT_ID_INTERNAL=$(echo "$CLIENT_RESPONSE" | jq -r '.id // empty')
  if [ -z "$CLIENT_ID_INTERNAL" ]; then
    # Location header contains the id
    CLIENT_ID_INTERNAL=$(curl -s -X POST "${KEYCLOAK_URL}/admin/realms/${REALM}/clients" \
      -H "$(auth_header)" \
      -H "Content-Type: application/json" \
      -d "$CLIENT_JSON" -D - | grep -i "^Location:" | sed 's/.*clients\///' | tr -d '\r')
  fi
else
  CLIENT_ID_INTERNAL="$EXISTING_CLIENT"
fi

echo "Client internal id: ${CLIENT_ID_INTERNAL}"

echo "==> Adding protocol mappers..."
# sub mapper
curl -s -X POST "${KEYCLOAK_URL}/admin/realms/${REALM}/clients/${CLIENT_ID_INTERNAL}/protocol-mappers/models" \
  -H "$(auth_header)" \
  -H "Content-Type: application/json" \
  -d "{
    \"name\": \"sub\",
    \"protocol\": \"openid-connect\",
    \"protocolMapper\": \"oidc-usermodel-property-mapper\",
    \"config\": {
      \"user.attribute\": \"username\",
      \"claim.name\": \"sub\",
      \"claim.type\": \"String\",
      \"id.token.claim\": \"true\",
      \"access.token.claim\": \"true\",
      \"jsonType.label\": \"String\"
    }
  }" > /dev/null || true

# preferred_username mapper
curl -s -X POST "${KEYCLOAK_URL}/admin/realms/${REALM}/clients/${CLIENT_ID_INTERNAL}/protocol-mappers/models" \
  -H "$(auth_header)" \
  -H "Content-Type: application/json" \
  -d "{
    \"name\": \"preferred_username\",
    \"protocol\": \"openid-connect\",
    \"protocolMapper\": \"oidc-usermodel-property-mapper\",
    \"config\": {
      \"user.attribute\": \"username\",
      \"claim.name\": \"preferred_username\",
      \"claim.type\": \"String\",
      \"id.token.claim\": \"true\",
      \"access.token.claim\": \"true\",
      \"jsonType.label\": \"String\"
    }
  }" > /dev/null || true

# audience mapper -> aud claim = client_id
curl -s -X POST "${KEYCLOAK_URL}/admin/realms/${REALM}/clients/${CLIENT_ID_INTERNAL}/protocol-mappers/models" \
  -H "$(auth_header)" \
  -H "Content-Type: application/json" \
  -d "{
    \"name\": \"audience\",
    \"protocol\": \"openid-connect\",
    \"protocolMapper\": \"oidc-audience-mapper\",
    \"config\": {
      \"included.client.audience\": \"${CLIENT_ID}\",
      \"id.token.claim\": \"true\",
      \"access.token.claim\": \"true\"
    }
  }" > /dev/null || true

echo "==> Creating test user '${TEST_USER}'..."
EXISTING_USER=$(curl -s -X GET "${KEYCLOAK_URL}/admin/realms/${REALM}/users?username=${TEST_USER}" \
  -H "$(auth_header)" | jq -r '.[0].id // empty')
if [ -z "$EXISTING_USER" ]; then
  curl -s -X POST "${KEYCLOAK_URL}/admin/realms/${REALM}/users" \
    -H "$(auth_header)" \
    -H "Content-Type: application/json" \
    -d "{
      \"username\": \"${TEST_USER}\",
      \"enabled\": true,
      \"emailVerified\": true,
      \"credentials\": [{\"type\": \"password\", \"value\": \"${TEST_PASS}\", \"temporary\": false}]
    }" > /dev/null
fi

echo "==> Done. Realm: ${REALM}, Client: ${CLIENT_ID}, Secret: ${CLIENT_SECRET}"
echo "==> Test token:"
TOKEN=$(curl -s -X POST "${KEYCLOAK_URL}/realms/${REALM}/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "username=${TEST_USER}" \
  -d "password=${TEST_PASS}" \
  -d "grant_type=password" \
  -d "client_id=${CLIENT_ID}" \
  -d "client_secret=${CLIENT_SECRET}" | jq -r '.access_token')
echo "$TOKEN" | jq -R 'split(".") | .[1] | @base64d | fromjson | {sub, preferred_username, aud, iss}'