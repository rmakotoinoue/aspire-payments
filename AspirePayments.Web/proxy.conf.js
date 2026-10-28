const target = process.env.API_HTTPS || process.env.API_HTTP;

if (!target) {
  throw new Error("Endpoint da API não configurado pelo Aspire.");
}

module.exports = {
  "/api": {
    target,
    secure: false,
    changeOrigin: true
  }
};
