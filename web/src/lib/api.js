import { http } from './http'

/** REST 接口封装：返回值直接是后端 JSON。 */
export const api = {
  login: (payload) => http.post('/api/auth/login', payload).then((response) => response.data),
  register: (payload) => http.post('/api/auth/register', payload).then((response) => response.data),
  me: () => http.get('/api/auth/me').then((response) => response.data),

  agents: () => http.get('/api/agents').then((response) => response.data),

  createConversation: (agentId) =>
    http.post('/api/conversations', { agentId }).then((response) => response.data),
  myConversations: () => http.get('/api/conversations/mine').then((response) => response.data),
  closeConversation: (id) =>
    http.post(`/api/conversations/${id}/close`).then((response) => response.data),
  messages: (id, afterSeq, limit) =>
    http
      .get(`/api/conversations/${id}/messages`, { params: { afterSeq, limit } })
      .then((response) => response.data),

  tickets: () => http.get('/api/tickets').then((response) => response.data),
  ticketMeta: () => http.get('/api/tickets/meta').then((response) => response.data),
  ticket: (id) => http.get(`/api/tickets/${id}`).then((response) => response.data),
  exportTicket: (id, format) =>
    http
      .get(`/api/tickets/${id}/export`, { params: { format }, responseType: 'blob' })
      .then((response) => response.data)
}
